using Blocks.Genesis.DataAccess;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;
using System.Text.RegularExpressions;

namespace Blocks.Genesis.DataAccess.Evaluation;

/// <summary>
/// Helper class for evaluating data access policies and building MongoDB filters.
/// Handles both token-based validation and data-level filtering.
/// </summary>
public static class DataAccessPolicyHelper
{
    private static readonly Regex ExpressionPattern = new(@"\{\{(\w+)\.(\w+)\}\}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

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
            return new PolicyEvaluationResult { IsAccessGranted = false };
        }

        var applicablePolicies = policies
            .Where(p => p.PolicyType == policyType &&
                       (p.Operation == operation || p.Operation == PolicyOperation.ALL))
            .OrderByDescending(p => p.Priority)
            .ToList();

        if (applicablePolicies.Count == 0)
        {
            return new PolicyEvaluationResult { IsAccessGranted = false };
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
                    return new PolicyEvaluationResult
                    {
                        IsAccessGranted = false,
                        ErrorMessage = $"Access denied by policy: {policy.PolicyName}"
                    };
                }
            }

            excludedFields.AddRange(result.ExcludedFields);
        }

        // If no allow policy granted access, deny
        if (!anyPolicyGranted)
        {
            return new PolicyEvaluationResult
            {
                IsAccessGranted = false,
                ErrorMessage = "No policy grants access to this resource."
            };
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

            if (ConvertToStringArray(compareValue) is not null &&
                IsCollectionOperator(rule.Operator))
            {
                return new PolicyEvaluationResult
                {
                    IsAccessGranted = true,
                    DataFilter = BuildArrayComparisonFilter(
                        FieldArrayExpression(rule.LeftOperand),
                        ConvertToBsonValue(compareValue),
                        rule.Operator),
                    RequiresDataFilter = true
                };
            }

            var dataFilter = BuildConditionFilter(principal, rule.LeftOperand, rule.Operator, compareValue);

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
            var dataFilter = BuildConditionFilter(principal, rule.RightOperand,
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
            "permissions" or "permission" => principal.Permissions,
            "organizationid" or "organization" => principal.OrganizationId,
            _ => GetCustomClaim(principal, propertyName)
        };
    }

    /// <summary>
    /// Gets a custom claim from the principal.
    /// Override this method if the principal gains custom claims.
    /// </summary>
    private static object? GetCustomClaim(AccessPrincipal principal, string claimName)
    {
        // Extend this if AccessPrincipal gains a Claims dictionary or similar
        return null;
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
            PolicyOperator.GREATER_THAN => CompareNumeric(leftValue, rightValue) > 0,
            PolicyOperator.GREATER_THAN_OR_EQUAL => CompareNumeric(leftValue, rightValue) >= 0,
            PolicyOperator.LESS_THAN => CompareNumeric(leftValue, rightValue) < 0,
            PolicyOperator.LESS_THAN_OR_EQUAL => CompareNumeric(leftValue, rightValue) <= 0,
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

        return left.Equals(right);
    }

    private static bool CompareContains(object left, object right)
    {
        if (left is string leftStr && right is string rightStr)
            return leftStr.Contains(rightStr, StringComparison.OrdinalIgnoreCase);

        return false;
    }

    private static bool CompareIn(object left, object right)
    {
        var leftStr = left?.ToString();
        if (string.IsNullOrEmpty(leftStr)) return false;

        // Try to convert right to string array
        var rightArray = ConvertToStringArray(right);
        if (rightArray != null)
        {
            return rightArray.Contains(leftStr, StringComparer.OrdinalIgnoreCase);
        }

        return false;
    }

    private static int CompareNumeric(object left, object right)
    {
        try
        {
            var leftDouble = Convert.ToDouble(left);
            var rightDouble = Convert.ToDouble(right);
            return leftDouble.CompareTo(rightDouble);
        }
        catch
        {
            return string.Compare(left.ToString(), right.ToString(), StringComparison.Ordinal);
        }
    }

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
