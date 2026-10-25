using Blocks.Genesis.DataAccess;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Blocks.Genesis.DataAccess.Evaluation;

/// <summary>
/// Helper class for evaluating data access policies and building MongoDB filters.
/// Handles both token-based validation and data-level filtering.
/// </summary>
public static class DataAccessPolicyHelper
{
    // {{Auth.UserId}}, {{token.Roles}}, and {{Auth.Attributes.<name>}} (one extra segment for access attributes).
    private static readonly Regex ExpressionPattern = new(@"\{\{(\w+)\.(\w+(?:\.[\w-]+)?)\}\}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>The Auth value name prefix for access attributes: <c>Attributes.&lt;name&gt;</c>.</summary>
    public const string AttributesPrefix = "Attributes.";

    /// <summary>The Auth value names a <c>{{Auth.X}}</c> / <c>{{token.X}}</c> expression inside <paramref name="text"/> refers to.</summary>
    public static IEnumerable<string> AuthExpressionNames(string? text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;
        foreach (Match match in ExpressionPattern.Matches(text))
        {
            var context = match.Groups[1].Value;
            if (string.Equals(context, "auth", StringComparison.OrdinalIgnoreCase) || string.Equals(context, "token", StringComparison.OrdinalIgnoreCase))
                yield return match.Groups[2].Value;
        }
    }

    #region Policy Evaluation

    /// <summary>
    /// Evaluates multiple policies for a schema and operation.
    /// Returns combined result with access decision and data filter.
    /// </summary>
    public static PolicyEvaluationResult EvaluatePolicies(
        this List<DataAccessPolicy> policies,
        AccessPrincipal principal,
        PolicyOperation operation,
        PolicyType policyType)
    {
        if (policies == null || policies.Count == 0)
        {
            return PolicyEvaluationResult.Forbidden(PolicyErrorCodes.NoPolicyGrantsAccess);
        }

        var applicablePolicies = policies
            .Where(p => p.PolicyType == policyType &&
                       (p.Operation == operation || p.Operation == PolicyOperation.ALL))
            .OrderByDescending(p => p.Priority)
            .ToList();

        if (applicablePolicies.Count == 0)
        {
            return PolicyEvaluationResult.Forbidden(PolicyErrorCodes.NoPolicyGrantsAccess);
        }

        var combinedFilters = new List<BsonDocument>();
        var excludedFields = new List<string>();
        var anyPolicyGranted = false;
        var requiresDataFilter = false;
        var grantsFullAccess = false;

        foreach (var policy in applicablePolicies)
        {
            var result = EvaluatePolicy(policy, principal);

            if (policy.IsAllowPolicy)
            {
                if (result.IsAccessGranted)
                {
                    anyPolicyGranted = true;

                    if (result.RequiresDataFilter && result.DataFilter.ElementCount > 0)
                    {
                        combinedFilters.Add(result.DataFilter);
                        requiresDataFilter = true;
                    }
                    else if (!result.RequiresDataFilter)
                    {
                        // Token-only validation passed without a data filter: this policy grants
                        // every row, so other policies' filters must not narrow it. Keep looping so
                        // deny policies and excluded fields are still applied.
                        grantsFullAccess = true;
                    }
                }
            }
            else
            {
                // Deny policy - if conditions match, deny access
                if (result.IsAccessGranted)
                {
                    return PolicyEvaluationResult.Forbidden($"Access denied by policy: {policy.PolicyName}");
                }
            }

            excludedFields.AddRange(result.ExcludedFields);
        }

        // If no allow policy granted access, deny
        if (!anyPolicyGranted)
        {
            return PolicyEvaluationResult.Forbidden(PolicyErrorCodes.NoPolicyGrantsAccess);
        }

        if (grantsFullAccess)
        {
            return new PolicyEvaluationResult
            {
                IsAccessGranted = true,
                ExcludedFields = excludedFields.Distinct().ToList(),
                RequiresDataFilter = false
            };
        }

        // Combine all filters with OR (any policy's filter condition grants access)
        var finalFilter = combinedFilters.Count switch
        {
            0 => new BsonDocument(),
            1 => combinedFilters[0],
            _ => new BsonDocument("$or", new BsonArray(combinedFilters))
        };

        return new PolicyEvaluationResult
        {
            IsAccessGranted = true,
            DataFilter = finalFilter,
            ExcludedFields = excludedFields.Distinct().ToList(),
            RequiresDataFilter = requiresDataFilter
        };
    }

    /// <summary>
    /// Evaluates a single policy and returns the result.
    /// </summary>
    public static PolicyEvaluationResult EvaluatePolicy(this DataAccessPolicy policy, AccessPrincipal principal)
    {
        var ruleGroup = policy.RuleGroup;

        if (ruleGroup == null ||
            (ruleGroup.Rules.Count == 0 && ruleGroup.NestedGroups.Count == 0))
        {
            return new PolicyEvaluationResult { IsAccessGranted = false };
        }

        return EvaluateRuleGroup(principal, ruleGroup);
    }

    /// <summary>
    /// Evaluates a rule group recursively.
    /// </summary>
    private static PolicyEvaluationResult EvaluateRuleGroup(AccessPrincipal principal, PolicyRuleGroup group)
    {
        var results = new List<PolicyEvaluationResult>();

        // Evaluate individual rules
        foreach (var rule in group.Rules)
        {
            results.Add(EvaluateRule(principal, rule));
        }

        // Evaluate nested groups recursively
        foreach (var nestedGroup in group.NestedGroups)
        {
            results.Add(EvaluateRuleGroup(principal, nestedGroup));
        }

        if (results.Count == 0)
        {
            return new PolicyEvaluationResult { IsAccessGranted = false }; // No rules evaluated = deny (same as query CLS)
        }

        return CombineResults(results, group.LogicalOperator);
    }

    /// <summary>
    /// Evaluates a single rule and returns the result.
    /// Token-only rules are evaluated immediately.
    /// Rules involving schema fields produce data filters.
    /// </summary>
    private static PolicyEvaluationResult EvaluateRule(AccessPrincipal principal, PolicyRule rule)
    {
        var leftIsToken = rule.LeftSource == ConditionSource.AUTH;
        var leftIsSchema = rule.LeftSource == ConditionSource.SCHEMA_FIELD;
        var rightIsSchema = rule.RightSource == ConditionSource.SCHEMA_FIELD;

        // Case 1: Token vs Static/Token - Pure validation (no data filter needed)
        if (leftIsToken && !leftIsSchema && !rightIsSchema)
        {
            var tokenValue = GetTokenValue(principal, rule.LeftOperand);
            var compareValue = rule.RightSource == ConditionSource.AUTH
                ? GetTokenValue(principal, rule.RightOperand)
                : rule.StaticValue;

            var isValid = EvaluateCondition(tokenValue, rule.Operator, compareValue);

            return new PolicyEvaluationResult
            {
                IsAccessGranted = isValid,
                RequiresDataFilter = false
            };
        }

        // Case 2: Schema field vs Token/Static - Requires data filter
        if (leftIsSchema)
        {
            var compareValue = rule.RightSource == ConditionSource.AUTH
                ? GetTokenValue(principal, rule.RightOperand)
                : rule.RightSource == ConditionSource.SCHEMA_FIELD
                    ? null // Schema vs Schema comparison (rare case)
                    : rule.StaticValue;

            // If comparing with another schema field, this needs special handling
            if (rule.RightSource == ConditionSource.SCHEMA_FIELD)
            {
                // MongoDB $expr for field-to-field comparison
                var filter = BuildFieldComparisonFilter(rule.LeftOperand, rule.Operator, rule.RightOperand);
                return new PolicyEvaluationResult
                {
                    IsAccessGranted = true, // Can't validate without data
                    DataFilter = filter,
                    RequiresDataFilter = true
                };
            }

            // In / Not in a typed list ("US,ZZ", or the rightOperands array): a plain $in / $nin, which matches a
            // single-value field and any item of a list field, and can use an index.
            if (rule.RightSource == ConditionSource.STATIC_VALUE &&
                rule.Operator is PolicyOperator.IN or PolicyOperator.NOT_IN &&
                StaticList(rule) is { } list)
            {
                return new PolicyEvaluationResult
                {
                    IsAccessGranted = true,
                    DataFilter = new BsonDocument(rule.LeftOperand, new BsonDocument(
                        rule.Operator == PolicyOperator.IN ? "$in" : "$nin", WithTypedAlternatives(list))),
                    RequiresDataFilter = true
                };
            }

            if (ConvertToStringArray(compareValue) is { } values &&
                IsCollectionOperator(rule.Operator))
            {
                return new PolicyEvaluationResult
                {
                    IsAccessGranted = true,
                    DataFilter = BuildArrayComparisonFilter(
                        FieldArrayExpression(rule.LeftOperand),
                        new BsonArray(values),
                        rule.Operator),
                    RequiresDataFilter = true
                };
            }

            var dataFilter = rule.RightSource == ConditionSource.STATIC_VALUE
                ? BuildStaticConditionFilter(principal, rule.LeftOperand, rule.Operator, compareValue)
                : BuildConditionFilter(principal, rule.LeftOperand, rule.Operator, compareValue);

            return new PolicyEvaluationResult
            {
                IsAccessGranted = true, // Access depends on data filter results
                DataFilter = dataFilter,
                RequiresDataFilter = true
            };
        }

        // Case 3: Static vs Schema (reversed operands - normalize)
        if (rule.LeftSource == ConditionSource.STATIC_VALUE && rightIsSchema)
        {
            var dataFilter = BuildStaticConditionFilter(principal, rule.RightOperand,
                GetReversedOperator(rule.Operator), rule.StaticValue);

            return new PolicyEvaluationResult
            {
                IsAccessGranted = true,
                DataFilter = dataFilter,
                RequiresDataFilter = true
            };
        }

        // Case 4: Token vs Schema field - Build filter comparing schema field to token value
        // Example: token.userId == CreatedBy -> { "CreatedBy": "user-id-value" }
        if (leftIsToken && rightIsSchema)
        {
            var tokenValue = GetTokenValue(principal, rule.LeftOperand);

            // If token value is null or empty, the rule cannot be satisfied
            if (tokenValue is null || (tokenValue is string s && string.IsNullOrEmpty(s)))
            {
                return new PolicyEvaluationResult
                {
                    IsAccessGranted = false,
                    ErrorMessage = $"Token value '{rule.LeftOperand}' is null or empty."
                };
            }

            // Build filter: SchemaField <operator> TokenValue
            // We need to reverse the operator since we're building: SchemaField op TokenValue
            // from the rule: TokenValue op SchemaField
            var rightOperands = rule.RightOperands.Count > 0 ? rule.RightOperands : [rule.RightOperand];
            var tokenArray = ConvertToStringArray(tokenValue);
            var filters = rightOperands.Select(rightOperand =>
                tokenArray is not null && IsCollectionOperator(rule.Operator)
                    ? BuildArrayComparisonFilter(
                        new BsonArray(tokenArray),
                        FieldArrayExpression(rightOperand),
                        rule.Operator)
                    : BuildConditionFilter(principal, rightOperand, GetReversedOperator(rule.Operator), tokenValue)).ToList();
            var combineOperator = rule.Operator is PolicyOperator.NOT_IN or PolicyOperator.NOT_CONTAIN
                ? "$and"
                : "$or";
            var dataFilter = filters.Count == 1
                ? filters[0]
                : new BsonDocument(combineOperator, new BsonArray(filters));

            return new PolicyEvaluationResult
            {
                IsAccessGranted = true,
                DataFilter = dataFilter,
                RequiresDataFilter = true
            };
        }

        return new PolicyEvaluationResult { IsAccessGranted = true };
    }

    /// <summary>
    /// Combines multiple evaluation results based on logical operator.
    /// </summary>
    private static PolicyEvaluationResult CombineResults(
        List<PolicyEvaluationResult> results,
        PolicyLogicalOperator logicalOperator)
    {
        if (results.Count == 0)
        {
            return new PolicyEvaluationResult { IsAccessGranted = true };
        }

        if (results.Count == 1)
        {
            return results[0];
        }

        // Separate token-only results from data filter results
        var tokenOnlyResults = results.Where(r => !r.RequiresDataFilter).ToList();
        var dataFilterResults = results.Where(r => r.RequiresDataFilter).ToList();

        if (logicalOperator == PolicyLogicalOperator.AND)
        {
            // AND: All token validations must pass
            var allTokensPassed = tokenOnlyResults.All(r => r.IsAccessGranted);

            if (!allTokensPassed)
            {
                return new PolicyEvaluationResult
                {
                    IsAccessGranted = false,
                    ErrorMessage = "Token validation failed."
                };
            }

            // Combine data filters with AND
            var filters = dataFilterResults
                .Where(r => r.DataFilter.ElementCount > 0)
                .Select(r => r.DataFilter)
                .ToList();

            var combinedFilter = filters.Count switch
            {
                0 => new BsonDocument(),
                1 => filters[0],
                _ => new BsonDocument("$and", new BsonArray(filters))
            };

            return new PolicyEvaluationResult
            {
                IsAccessGranted = true,
                DataFilter = combinedFilter,
                RequiresDataFilter = dataFilterResults.Any(),
                ExcludedFields = results.SelectMany(r => r.ExcludedFields).Distinct().ToList()
            };
        }
        else // OR
        {
            // OR: Any token validation passing grants access (possibly with filter)
            var anyTokenPassed = tokenOnlyResults.Any(r => r.IsAccessGranted);

            if (anyTokenPassed && !dataFilterResults.Any())
            {
                // A token-only rule passed - full access granted
                return new PolicyEvaluationResult
                {
                    IsAccessGranted = true,
                    RequiresDataFilter = false
                };
            }

            // If any token-only result passed, that branch grants full access
            // The data filters are only needed for branches where token didn't pass
            var filters = dataFilterResults
                .Where(result => result.DataFilter.ElementCount > 0)
                .Select(result => result.DataFilter)
                .ToList();

            // If a token-only passed, we have full access OR filtered access
            if (anyTokenPassed)
            {
                return new PolicyEvaluationResult
                {
                    IsAccessGranted = true,
                    RequiresDataFilter = false, // Token grants full access
                    ExcludedFields = results.SelectMany(r => r.ExcludedFields).Distinct().ToList()
                };
            }

            // No token-only passed, all access depends on data filters
            if (tokenOnlyResults.Any() && !anyTokenPassed && !dataFilterResults.Any())
            {
                return new PolicyEvaluationResult
                {
                    IsAccessGranted = false,
                    ErrorMessage = "No conditions grant access."
                };
            }

            var combinedFilter = filters.Count switch
            {
                0 => new BsonDocument(),
                1 => filters[0],
                _ => new BsonDocument("$or", new BsonArray(filters))
            };

            return new PolicyEvaluationResult
            {
                IsAccessGranted = filters.Count > 0 || anyTokenPassed,
                DataFilter = combinedFilter,
                RequiresDataFilter = filters.Count > 0,
                ExcludedFields = results.SelectMany(r => r.ExcludedFields).Distinct().ToList()
            };
        }
    }

    #endregion

    #region CLS path helpers (nested properties)

    /// <summary>
    /// Returns true if rowPath matches schemaPath; schema paths have no array indices (e.g. "Courses.Description"),
    /// row paths may have indices (e.g. "Courses.0.Description"). When the schema path is fully consumed, any extra
    /// row segments must be numeric indices only (e.g. "Skills" matches "Skills.0"), so scalar paths like
    /// "Contact.Email" do not match an allowed schema path of just "Contact".
    /// </summary>
    public static bool RowPathMatchesSchemaPath(string schemaPath, string rowPath)
    {
        if (string.IsNullOrEmpty(schemaPath) || string.IsNullOrEmpty(rowPath)) return false;
        if (schemaPath == rowPath) return true;
        var schemaSegs = schemaPath.Split('.');
        var rowSegs = rowPath.Split('.');
        int si = 0, ri = 0;
        while (si < schemaSegs.Length && ri < rowSegs.Length)
        {
            if (string.Equals(schemaSegs[si], rowSegs[ri], StringComparison.Ordinal))
            {
                si++;
                ri++;
                continue;
            }
            if (int.TryParse(rowSegs[ri], out _))
            {
                ri++;
                continue;
            }
            return false;
        }
        if (si != schemaSegs.Length)
            return false;
        while (ri < rowSegs.Length)
        {
            if (!int.TryParse(rowSegs[ri], out _))
                return false;
            ri++;
        }
        return true;
    }

    /// <summary>
    /// True if READ CLS lists this schema path, a listed path extends it, or this path is under a listed prefix.
    /// RLS-derived CLS is attached only to scalar leaves (e.g. <c>Courses.Title</c>), so container paths like
    /// <c>Courses</c> must count as covered when any listed field is <c>Courses.*</c>.
    /// </summary>
    public static bool IsPathCoveredByClsFieldNames(string path, HashSet<string> clsFieldNames)
    {
        if (string.IsNullOrEmpty(path) || clsFieldNames == null || clsFieldNames.Count == 0)
            return false;
        if (clsFieldNames.Contains(path)) return true;
        if (clsFieldNames.Any(f => !string.IsNullOrEmpty(f) && path.StartsWith(f + ".", StringComparison.Ordinal)))
            return true;
        return clsFieldNames.Any(f => !string.IsNullOrEmpty(f) && f.StartsWith(path + ".", StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns true if the path is protected by CLS (i.e. it is exactly one of the policy field names or a nested path under one).
    /// Schema paths (e.g. "Courses.Description") match row paths with array indices (e.g. "Courses.0.Description").
    /// </summary>
    public static bool IsPathProtectedByCls(string path, HashSet<string> protectedFieldNames)
    {
        if (string.IsNullOrEmpty(path) || protectedFieldNames == null || protectedFieldNames.Count == 0)
            return false;
        if (protectedFieldNames.Contains(path)) return true;
        if (protectedFieldNames.Any(f => !string.IsNullOrEmpty(f) && path.StartsWith(f + ".", StringComparison.Ordinal)))
            return true;
        if (protectedFieldNames.Any(f => !string.IsNullOrEmpty(f) && f.StartsWith(path + ".", StringComparison.Ordinal)))
            return true;
        return protectedFieldNames.Any(f => !string.IsNullOrEmpty(f) && RowPathMatchesSchemaPath(f, path));
    }

    /// <summary>
    /// Returns true if the path is allowed by the allowed set: exact match or path is under an allowed field name.
    /// Schema paths (e.g. "Courses.Description") match row paths with array indices (e.g. "Courses.0.Description").
    /// When the path is protected by CLS, only explicit allowance applies (nested path's policy has priority over parent).
    /// </summary>
    public static bool IsPathAllowed(string path, HashSet<string> allowedFieldNames, HashSet<string>? fieldNamesProtectedByCls = null)
    {
        if (allowedFieldNames == null || allowedFieldNames.Count == 0) return false;

        var isProtected = fieldNamesProtectedByCls != null && fieldNamesProtectedByCls.Count > 0 && IsPathProtectedByCls(path, fieldNamesProtectedByCls);

        if (isProtected)
        {
            // For CLS-protected paths, allow this path if it matches an allowed field or is an ancestor prefix of one
            // (e.g. Courses when Courses.Title is allowed — CLS is stored on leaves only).
            if (allowedFieldNames.Contains(path)) return true;
            if (allowedFieldNames.Any(a => !string.IsNullOrEmpty(a) && a.StartsWith(path + ".", StringComparison.Ordinal)))
                return true;
            return allowedFieldNames.Any(a => !string.IsNullOrEmpty(a) && RowPathMatchesSchemaPath(a, path));
        }

        if (allowedFieldNames.Contains(path)) return true;
        if (allowedFieldNames.Any(a => !string.IsNullOrEmpty(a) && (path == a || path.StartsWith(a + ".", StringComparison.Ordinal))))
            return true;
        if (allowedFieldNames.Any(a => !string.IsNullOrEmpty(a) && a.StartsWith(path + ".", StringComparison.Ordinal)))
            return true;
        return allowedFieldNames.Any(a => !string.IsNullOrEmpty(a) && RowPathMatchesSchemaPath(a, path));
    }

    #endregion

    #region Token Value Resolution

    /// <summary>
    /// Gets a value from the caller's authentication token (the principal captured for the request).
    /// </summary>
    public static object? GetTokenValue(AccessPrincipal principal, string propertyName)
    {
        if (principal is null || !principal.HasContext)
            return null;

        return propertyName.ToLowerInvariant() switch
        {
            "userid" or "id" or "sub" => principal.UserId,
            "email" => principal.UserName,
            "tenantid" or "tenant" => principal.TenantId,
            "roles" or "role" => principal.Roles,
            "permissions" or "permission" => principal.EffectivePermissions,
            "organizationid" or "organization" => principal.OrganizationId,
            _ => GetCustomClaim(principal, propertyName)
        };
    }

    /// <summary>
    /// Names that are not built in. <c>Attributes.&lt;name&gt;</c> is the caller's access attribute values for their
    /// organization, as read from the database (<see cref="AccessPrincipal.AccessAttributes"/>), never from token
    /// claims; a name the caller has no values for, and any other unknown name, is null.
    /// </summary>
    private static string[]? GetCustomClaim(AccessPrincipal principal, string claimName)
    {
        if (!claimName.StartsWith(AttributesPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var name = claimName[AttributesPrefix.Length..];
        if (name.Length == 0 || principal.AccessAttributes is null)
            return null;

        return principal.AccessAttributes.TryGetValue(name, out var values) ? values.ToArray() : null;
    }

    #endregion

    #region Condition Evaluation

    /// <summary>
    /// Evaluates a condition between two values.
    /// </summary>
    public static bool EvaluateCondition(object? leftValue, PolicyOperator op, object? rightValue)
    {
        // Handle null cases
        if (op == PolicyOperator.IS_NULL)
            return leftValue is null || (leftValue is string s && string.IsNullOrEmpty(s));

        if (op == PolicyOperator.IS_NOT_NULL)
            return leftValue is not null && !(leftValue is string s && string.IsNullOrEmpty(s));

        if (leftValue is null || rightValue is null)
            return false;

        // Principal selectors send multiple values as one comma-delimited
        // string. For scalar equality, treat those values as alternatives.
        var commaDelimitedRight = ConvertToStringArray(rightValue);
        if (ConvertToStringArray(leftValue) is null && commaDelimitedRight is { Length: > 0 })
        {
            return op switch
            {
                PolicyOperator.EQUAL => commaDelimitedRight.Any(value => CompareEquals(leftValue, value)),
                PolicyOperator.NOT_EQUAL => commaDelimitedRight.All(value => !CompareEquals(leftValue, value)),
                _ => EvaluateConditionWithoutPrincipalAlternatives(leftValue, op, rightValue)
            };
        }

        // Handle array-based comparisons (for Roles, Permissions)
        // Try to convert left value to string array (handles object[], List<object>, etc.)
        var leftArray = ConvertToStringArray(leftValue);
        if (leftArray != null && leftArray.Length > 0)
        {
            return EvaluateArrayCondition(leftArray, op, rightValue);
        }

        // Standard comparisons
        return op switch
        {
            PolicyOperator.EQUAL => CompareEquals(leftValue, rightValue),
            PolicyOperator.NOT_EQUAL => !CompareEquals(leftValue, rightValue),
            PolicyOperator.CONTAIN => CompareContains(leftValue, rightValue),
            PolicyOperator.NOT_CONTAIN => !CompareContains(leftValue, rightValue),
            PolicyOperator.IN => CompareIn(leftValue, rightValue),
            PolicyOperator.NOT_IN => !CompareIn(leftValue, rightValue),
            PolicyOperator.GREATER_THAN => CompareOrdered(leftValue, rightValue) > 0,
            PolicyOperator.GREATER_THAN_OR_EQUAL => CompareOrdered(leftValue, rightValue) >= 0,
            PolicyOperator.LESS_THAN => CompareOrdered(leftValue, rightValue) < 0,
            PolicyOperator.LESS_THAN_OR_EQUAL => CompareOrdered(leftValue, rightValue) <= 0,
            PolicyOperator.START_WITH => leftValue.ToString()?.StartsWith(rightValue.ToString() ?? "") ?? false,
            PolicyOperator.END_WITH => leftValue.ToString()?.EndsWith(rightValue.ToString() ?? "") ?? false,
            PolicyOperator.REGEX => Regex.IsMatch(leftValue.ToString() ?? "", rightValue.ToString() ?? "", RegexOptions.None, TimeSpan.FromSeconds(1)),
            _ => false
        };
    }

    private static bool EvaluateConditionWithoutPrincipalAlternatives(
        object leftValue, PolicyOperator op, object rightValue) => op switch
    {
        PolicyOperator.CONTAIN => CompareContains(leftValue, rightValue),
        PolicyOperator.NOT_CONTAIN => !CompareContains(leftValue, rightValue),
        PolicyOperator.IN => CompareIn(leftValue, rightValue),
        PolicyOperator.NOT_IN => !CompareIn(leftValue, rightValue),
        _ => false
    };

    /// <summary>
    /// Evaluates conditions where the left operand is an array (e.g., Roles, Permissions).
    /// 
    /// Operator behaviors for array comparisons:
    /// - EQUAL: Both arrays have exactly the same elements in the same order
    /// - NOT_EQUAL: Arrays are different
    /// - CONTAIN: ALL elements from right array exist in left array
    /// - NOT_CONTAIN: NONE of the elements from right array exist in left array
    /// - IN: ANY element from left array exists in right array (intersection check)
    /// </summary>
    private static bool EvaluateArrayCondition(string[] leftArray, PolicyOperator op, object? rightValue)
    {
        // Try to convert right value to string array (handles object[], List<object>, BsonArray, etc.)
        var rightArray = ConvertToStringArray(rightValue);
        if (rightArray != null && rightArray.Length > 0)
        {
            return op switch
            {
                // Exact match - same elements in same order
                PolicyOperator.EQUAL => leftArray.SequenceEqual(rightArray, StringComparer.OrdinalIgnoreCase),
                PolicyOperator.NOT_EQUAL => !leftArray.SequenceEqual(rightArray, StringComparer.OrdinalIgnoreCase),

                // CONTAIN: ALL right elements must be in left array
                PolicyOperator.CONTAIN => rightArray.All(r => leftArray.Contains(r, StringComparer.OrdinalIgnoreCase)),

                // NOT_CONTAIN: NONE of right elements should be in left array
                PolicyOperator.NOT_CONTAIN => !rightArray.Any(r => leftArray.Contains(r, StringComparer.OrdinalIgnoreCase)),

                // IN: ANY left element exists in right array (checks for intersection)
                // Example: auth roles ["user", "hr-admin"] IN static roles ["admin", "hr-admin", "manager"]
                // Returns TRUE because "hr-admin" is in both arrays
                PolicyOperator.IN => leftArray.Any(l => rightArray.Contains(l, StringComparer.OrdinalIgnoreCase)),
                PolicyOperator.NOT_IN => !leftArray.Any(l => rightArray.Contains(l, StringComparer.OrdinalIgnoreCase)),

                _ => false
            };
        }

        // Handle a single (non-comma-delimited) string value on the right.
        if (rightValue is string stringValue)
        {
            return op switch
            {
                PolicyOperator.EQUAL => leftArray.Length == 1 && string.Equals(leftArray[0], stringValue, StringComparison.OrdinalIgnoreCase),
                PolicyOperator.NOT_EQUAL => !(leftArray.Length == 1 && string.Equals(leftArray[0], stringValue, StringComparison.OrdinalIgnoreCase)),
                PolicyOperator.CONTAIN => leftArray.Contains(stringValue, StringComparer.OrdinalIgnoreCase),
                PolicyOperator.NOT_CONTAIN => !leftArray.Contains(stringValue, StringComparer.OrdinalIgnoreCase),
                PolicyOperator.IN => leftArray.Contains(stringValue, StringComparer.OrdinalIgnoreCase),
                _ => false
            };
        }

        return false;
    }

    /// <summary>
    /// Converts various array/enumerable types to string[].
    /// Handles object[], List&lt;object&gt;, BsonArray, IEnumerable&lt;string&gt;, etc.
    /// </summary>
    private static string[]? ConvertToStringArray(object? value)
    {
        if (value == null) return null;

        // Already string[]
        if (value is string[] strArray)
            return strArray;

        // Comma-delimited values are the wire format used by the policy editor
        // for multi-select right-side operands. A plain string remains scalar.
        if (value is string stringValue)
        {
            if (!stringValue.Contains(',')) return null;
            var values = stringValue.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return values.Length > 0 ? values : null;
        }

        // IEnumerable<string>
        if (value is IEnumerable<string> strEnumerable)
            return strEnumerable.ToArray();

        // object[] - common from JSON deserialization
        if (value is object[] objArray)
            return objArray.Select(o => o?.ToString() ?? "").ToArray();

        // IEnumerable<object> or IList - common from JSON/BSON deserialization
        if (value is System.Collections.IEnumerable enumerable)
        {
            var result = new List<string>();
            foreach (var item in enumerable)
            {
                result.Add(item?.ToString() ?? "");
            }
            return result.Count > 0 ? result.ToArray() : null;
        }

        return null;
    }

    private static bool CompareEquals(object left, object right)
    {
        if (left is string leftStr && right is string rightStr)
            return string.Equals(leftStr, rightStr, StringComparison.OrdinalIgnoreCase);

        // A value typed in the policy editor arrives as text, while the row holds a real bool, number or date.
        if (right is string rightText)
            return TypedEquals(left, rightText);
        if (left is string leftText)
            return TypedEquals(right, leftText);

        return left.Equals(right);
    }

    /// <summary>
    /// A row's bool, number or date against text: the text read as that type, the way
    /// <see cref="TypedAlternative"/> reads it for a filter. Any other value never equals text.
    /// </summary>
    private static bool TypedEquals(object typed, string text)
    {
        text = text.Trim();
        return typed switch
        {
            bool flag => bool.TryParse(text, out var parsed) && parsed == flag,
            DateTime date => ParseIsoDate(text) is { } parsed && AsUtc(date) == parsed,
            _ when IsNumber(typed) => ParseNumber(text) is { } number && Convert.ToDouble(typed, CultureInfo.InvariantCulture) == number,
            _ => typed.Equals(text)
        };
    }

    private static DateTime AsUtc(DateTime date) => date.Kind switch
    {
        DateTimeKind.Utc => date,
        // Stored dates are UTC; one without a kind is read as UTC, as MongoDB does.
        DateTimeKind.Unspecified => DateTime.SpecifyKind(date, DateTimeKind.Utc),
        _ => date.ToUniversalTime()
    };

    private static bool CompareContains(object left, object right)
    {
        if (left is string leftStr && right is string rightStr)
            return leftStr.Contains(rightStr, StringComparison.OrdinalIgnoreCase);

        return false;
    }

    private static bool CompareIn(object left, object right)
    {
        if (left is null || string.IsNullOrEmpty(left.ToString())) return false;

        // Try to convert right to string array; each item compares as Equal does (bool, number and date read as such).
        var rightArray = ConvertToStringArray(right);
        if (rightArray != null)
        {
            return rightArray.Any(item => CompareEquals(left, item));
        }

        return false;
    }

    /// <summary>
    /// Orders two values for Greater / Less than. A date and a date or ISO date text compare as dates, a number and
    /// a number or numeric text as numbers (text read the same way on every server, whatever its culture); anything
    /// else compares as text.
    /// </summary>
    private static int CompareOrdered(object left, object right)
    {
        if (AsDate(left) is { } leftDate && AsDate(right) is { } rightDate)
            return leftDate.CompareTo(rightDate);
        if (AsNumber(left) is { } leftNumber && AsNumber(right) is { } rightNumber)
            return leftNumber.CompareTo(rightNumber);
        return string.Compare(left.ToString(), right.ToString(), StringComparison.Ordinal);
    }

    private static DateTime? AsDate(object value) => value switch
    {
        DateTime date => AsUtc(date),
        string text => ParseIsoDate(text.Trim()),
        _ => null
    };

    private static double? AsNumber(object value) => value switch
    {
        string text => ParseNumber(text.Trim()),
        _ when IsNumber(value) => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        _ => null
    };

    private static bool IsNumber(object value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;

    #endregion

    #region MongoDB Filter Building

    /// <summary>
    /// Builds a MongoDB filter document for a single condition.
    /// </summary>
    public static BsonDocument BuildConditionFilter(AccessPrincipal principal, string fieldName, PolicyOperator op, object? value)
    {
        var commaDelimitedValues = ConvertToStringArray(value);
        if (commaDelimitedValues is { Length: > 0 })
        {
            if (op == PolicyOperator.EQUAL)
                return new BsonDocument(fieldName, new BsonDocument("$in", new BsonArray(commaDelimitedValues)));
            if (op == PolicyOperator.NOT_EQUAL)
                return new BsonDocument(fieldName, new BsonDocument("$nin", new BsonArray(commaDelimitedValues)));
        }

        var resolvedValue = ResolveBsonValue(principal, value);

        return op switch
        {
            PolicyOperator.EQUAL => new BsonDocument(fieldName, resolvedValue),
            PolicyOperator.NOT_EQUAL => new BsonDocument(fieldName, new BsonDocument("$ne", resolvedValue)),
            PolicyOperator.GREATER_THAN => new BsonDocument(fieldName, new BsonDocument("$gt", resolvedValue)),
            PolicyOperator.GREATER_THAN_OR_EQUAL => new BsonDocument(fieldName, new BsonDocument("$gte", resolvedValue)),
            PolicyOperator.LESS_THAN => new BsonDocument(fieldName, new BsonDocument("$lt", resolvedValue)),
            PolicyOperator.LESS_THAN_OR_EQUAL => new BsonDocument(fieldName, new BsonDocument("$lte", resolvedValue)),
            PolicyOperator.CONTAIN => new BsonDocument(fieldName, new BsonDocument("$regex", resolvedValue).Add("$options", "i")),
            PolicyOperator.NOT_CONTAIN => new BsonDocument(fieldName, new BsonDocument("$not", new BsonDocument("$regex", resolvedValue).Add("$options", "i"))),
            PolicyOperator.IN => new BsonDocument(fieldName, new BsonDocument("$in", ResolveArrayValue(principal, value))),
            PolicyOperator.NOT_IN => new BsonDocument(fieldName, new BsonDocument("$nin", ResolveArrayValue(principal, value))),
            PolicyOperator.START_WITH => new BsonDocument(fieldName, new BsonDocument("$regex", $"^{Regex.Escape(value?.ToString() ?? "")}").Add("$options", "i")),
            PolicyOperator.END_WITH => new BsonDocument(fieldName, new BsonDocument("$regex", $"{Regex.Escape(value?.ToString() ?? "")}$").Add("$options", "i")),
            PolicyOperator.IS_NULL => new BsonDocument(fieldName, BsonNull.Value),
            PolicyOperator.IS_NOT_NULL => new BsonDocument(fieldName, new BsonDocument("$ne", BsonNull.Value)),
            PolicyOperator.REGEX => new BsonDocument(fieldName, new BsonDocument("$regex", value?.ToString() ?? "")),
            _ => new BsonDocument()
        };
    }

    private static readonly Regex IsoDatePattern = new(@"^\d{4}-\d{2}-\d{2}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>
    /// <see cref="BuildConditionFilter"/> for a value typed in the policy editor. It arrives as text ("150", "true"),
    /// and a number, date or bool field never matches text in MongoDB, so a comparison against text that reads as a
    /// number or an ISO date (or, for Equal and Not equal, true or false) matches either the typed value or the text:
    /// number, date, bool and text fields all compare as written.
    /// </summary>
    private static BsonDocument BuildStaticConditionFilter(AccessPrincipal principal, string fieldName, PolicyOperator op, object? value)
    {
        var asText = BuildConditionFilter(principal, fieldName, op, value);
        var withBoolean = op is PolicyOperator.EQUAL or PolicyOperator.NOT_EQUAL;
        if (!IsScalarComparison(op) || TypedAlternative(value, withBoolean) is not { } typed)
            return asText;

        var asTyped = BuildConditionFilter(principal, fieldName, op, typed);
        // Not equal must differ from both forms; every other comparison may hold for either.
        return new BsonDocument(op == PolicyOperator.NOT_EQUAL ? "$and" : "$or", new BsonArray { asTyped, asText });
    }

    private static bool IsScalarComparison(PolicyOperator op) => op is
        PolicyOperator.EQUAL or PolicyOperator.NOT_EQUAL or
        PolicyOperator.GREATER_THAN or PolicyOperator.GREATER_THAN_OR_EQUAL or
        PolicyOperator.LESS_THAN or PolicyOperator.LESS_THAN_OR_EQUAL;

    /// <summary>
    /// The number, UTC date or (with <paramref name="withBoolean"/>) bool a typed static value reads as; null for
    /// other text, lists and token expressions. Bools are left out of ordering comparisons, where they mean nothing.
    /// </summary>
    private static object? TypedAlternative(object? value, bool withBoolean)
    {
        if (value is not string text || text.Contains(',') || ExpressionPattern.IsMatch(text))
            return null;
        text = text.Trim();
        if (ParseNumber(text) is { } number)
            return number;
        if (ParseIsoDate(text) is { } date)
            return date;
        if (withBoolean && bool.TryParse(text, out var flag))
            return flag;
        return null;
    }

    private static double? ParseNumber(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;

    private static DateTime? ParseIsoDate(string text) =>
        IsoDatePattern.IsMatch(text) &&
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;

    /// <summary>
    /// The values of an In / Not in rule against a typed list: the comma-delimited static value the editor sends,
    /// a stored list, or <see cref="PolicyRule.RightOperands"/>. Null for a token expression, which keeps its own path.
    /// </summary>
    private static string[]? StaticList(PolicyRule rule)
    {
        if (rule.StaticValue is string text)
        {
            if (ExpressionPattern.IsMatch(text))
                return null;
            var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
                return parts;
        }
        else if (rule.StaticValue is not null && ConvertToStringArray(rule.StaticValue) is { Length: > 0 } stored)
        {
            return stored;
        }

        var operands = rule.RightOperands.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).ToArray();
        return operands.Length > 0 ? operands : null;
    }

    /// <summary>Each value as text, plus its number, date or bool when it reads as one (see <see cref="BuildStaticConditionFilter"/>).</summary>
    private static BsonArray WithTypedAlternatives(string[] values)
    {
        var array = new BsonArray();
        foreach (var value in values)
        {
            array.Add(new BsonString(value));
            if (TypedAlternative(value, withBoolean: true) is { } typed)
                array.Add(ConvertToBsonValue(typed));
        }
        return array;
    }

    /// <summary>
    /// Builds a MongoDB filter for field-to-field comparison using $expr.
    /// </summary>
    private static BsonDocument BuildFieldComparisonFilter(
        string leftField,
        PolicyOperator op,
        string rightField)
    {
        if (IsCollectionOperator(op))
        {
            return BuildArrayComparisonFilter(
                FieldArrayExpression(leftField),
                FieldArrayExpression(rightField),
                op);
        }

        var mongoOp = op switch
        {
            PolicyOperator.EQUAL => "$eq",
            PolicyOperator.NOT_EQUAL => "$ne",
            PolicyOperator.GREATER_THAN => "$gt",
            PolicyOperator.GREATER_THAN_OR_EQUAL => "$gte",
            PolicyOperator.LESS_THAN => "$lt",
            PolicyOperator.LESS_THAN_OR_EQUAL => "$lte",
            _ => "$eq"
        };

        return new BsonDocument("$expr", new BsonDocument(mongoOp,
            new BsonArray { $"${leftField}", $"${rightField}" }));
    }

    private static bool IsCollectionOperator(PolicyOperator op) => op is
        PolicyOperator.CONTAIN or PolicyOperator.NOT_CONTAIN or
        PolicyOperator.IN or PolicyOperator.NOT_IN;

    private static BsonDocument FieldArrayExpression(string fieldName)
    {
        var field = new BsonString($"${fieldName}");
        return new BsonDocument("$cond", new BsonArray
        {
            new BsonDocument("$isArray", field),
            field,
            new BsonDocument("$cond", new BsonArray
            {
                new BsonDocument("$eq", new BsonArray { field, BsonNull.Value }),
                new BsonArray(),
                new BsonArray { field }
            })
        });
    }

    private static BsonDocument BuildArrayComparisonFilter(
        BsonValue left,
        BsonValue right,
        PolicyOperator op)
    {
        BsonValue expression = op switch
        {
            PolicyOperator.CONTAIN => new BsonDocument("$setIsSubset", new BsonArray { right, left }),
            PolicyOperator.NOT_CONTAIN => BuildNoArrayIntersectionExpression(left, right),
            PolicyOperator.IN => new BsonDocument("$gt", new BsonArray
            {
                new BsonDocument("$size", new BsonDocument("$setIntersection", new BsonArray { left, right })),
                0
            }),
            PolicyOperator.NOT_IN => BuildNoArrayIntersectionExpression(left, right),
            _ => BsonBoolean.False
        };

        return new BsonDocument("$expr", expression);
    }

    private static BsonDocument BuildNoArrayIntersectionExpression(BsonValue left, BsonValue right) =>
        new("$eq", new BsonArray
        {
            new BsonDocument("$size", new BsonDocument("$setIntersection", new BsonArray { left, right })),
            0
        });

    /// <summary>
    /// Gets the reversed operator for normalizing operand order.
    /// </summary>
    private static PolicyOperator GetReversedOperator(PolicyOperator op)
    {
        return op switch
        {
            PolicyOperator.GREATER_THAN => PolicyOperator.LESS_THAN,
            PolicyOperator.GREATER_THAN_OR_EQUAL => PolicyOperator.LESS_THAN_OR_EQUAL,
            PolicyOperator.LESS_THAN => PolicyOperator.GREATER_THAN,
            PolicyOperator.LESS_THAN_OR_EQUAL => PolicyOperator.GREATER_THAN_OR_EQUAL,
            _ => op // Symmetric operators remain the same
        };
    }

    #endregion

    #region Value Resolution

    /// <summary>
    /// Resolves a value to its BsonValue representation.
    /// Handles token expressions like {{token.Email}}.
    /// </summary>
    private static BsonValue ResolveBsonValue(AccessPrincipal principal, object? value)
    {
        if (value is null)
            return BsonNull.Value;

        if (value is string strValue)
        {
            // Check for token expression
            var match = ExpressionPattern.Match(strValue);
            if (match.Success)
            {
                var context = match.Groups[1].Value.ToLowerInvariant();
                var property = match.Groups[2].Value;

                if (context == "token" || context == "auth")
                {
                    var tokenValue = GetTokenValue(principal, property);
                    return ConvertToBsonValue(tokenValue);
                }
            }

            return new BsonString(strValue);
        }

        return ConvertToBsonValue(value);
    }

    /// <summary>
    /// Converts a CLR value to BsonValue.
    /// </summary>
    private static BsonValue ConvertToBsonValue(object? value)
    {
        return value switch
        {
            null => BsonNull.Value,
            string s => new BsonString(s),
            int i => new BsonInt32(i),
            long l => new BsonInt64(l),
            double d => new BsonDouble(d),
            decimal dec => new BsonDecimal128(dec),
            bool b => new BsonBoolean(b),
            DateTime dt => new BsonDateTime(dt),
            string[] arr => new BsonArray(arr),
            IEnumerable<string> enumerable => new BsonArray(enumerable),
            _ => new BsonString(value.ToString() ?? string.Empty)
        };
    }

    /// <summary>
    /// Resolves a value to a BsonArray.
    /// </summary>
    private static BsonArray ResolveArrayValue(AccessPrincipal principal, object? value)
    {
        if (value is null)
            return new BsonArray();

        if (value is string strValue)
        {
            // Check for token expression
            var match = ExpressionPattern.Match(strValue);
            if (match.Success)
            {
                var context = match.Groups[1].Value.ToLowerInvariant();
                var property = match.Groups[2].Value;

                if (context == "token" || context == "auth")
                {
                    var tokenValue = GetTokenValue(principal, property);
                    if (tokenValue is string[] arr)
                        return new BsonArray(arr);
                    if (tokenValue is IEnumerable<string> enumerable)
                        return new BsonArray(enumerable);
                    if (tokenValue != null)
                        return new BsonArray { ConvertToBsonValue(tokenValue) };
                }
            }

            return new BsonArray { new BsonString(strValue) };
        }

        if (value is IEnumerable<object> objEnumerable)
        {
            var bsonArray = new BsonArray();
            foreach (var item in objEnumerable)
            {
                bsonArray.Add(ConvertToBsonValue(item));
            }
            return bsonArray;
        }

        if (value is string[] strArr)
            return new BsonArray(strArr);

        return new BsonArray { ConvertToBsonValue(value) };
    }

    #endregion
}
