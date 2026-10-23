using Blocks.Genesis.DataAccess;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;

namespace Blocks.Genesis.DataAccess.Evaluation;

/// <summary>
/// Helpers for mutation input: path collection, path removal, and CLS field resolution.
/// The GraphQL-free part of the gateway's MutationInputHelper; the gateway keeps input parsing.
/// </summary>
public static class ClsInputHelper
{
    /// <summary>
    /// Collects all field paths from input (root and nested).
    /// E.g. {"ContactInfo": {"Email": "x"}} -> ["ContactInfo", "ContactInfo.Email"].
    /// </summary>
    public static List<string> GetAllPathsFromInput(IDictionary<string, object?> input, string prefix)
    {
        var paths = new List<string>();
        foreach (var kv in input)
        {
            var path = string.IsNullOrEmpty(prefix) ? kv.Key : prefix + "." + kv.Key;
            paths.Add(path);
            var value = kv.Value;
            if (value is IDictionary<string, object?> nested)
                paths.AddRange(GetAllPathsFromInput(nested, path));
            else if (value is IList<object?> list)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    var itemPath = path + "." + i;
                    paths.Add(itemPath);
                    var item = list[i];
                    if (item is IDictionary<string, object?> itemDict)
                        paths.AddRange(GetAllPathsFromInput(itemDict, itemPath));
                }
            }
        }
        return paths;
    }

    /// <summary>
    /// Removes excluded paths from input and returns the list of removed path names.
    /// </summary>
    public static List<string> RemoveExcludedPathsFromInput(IDictionary<string, object?> input, List<string> excludedPaths)
    {
        var removed = new List<string>();
        foreach (var path in excludedPaths)
        {
            if (RemovePathFromInput(input, path))
                removed.Add(path);
        }
        return removed;
    }

    /// <summary>
    /// Removes or nulls a path from input. Returns true if the path was present and removed.
    /// </summary>
    public static bool RemovePathFromInput(IDictionary<string, object?> item, string path)
    {
        var dotIndex = path.IndexOf('.');
        if (dotIndex < 0)
        {
            if (item.ContainsKey(path))
            {
                item.Remove(path);
                return true;
            }
            return false;
        }
        var segment = path[..dotIndex];
        var rest = path[(dotIndex + 1)..];
        if (!item.TryGetValue(segment, out var segmentValue) || segmentValue == null)
            return false;
        if (segmentValue is IDictionary<string, object?> nested)
            return RemovePathFromInput(nested, rest);
        if (segmentValue is IList<object?> list)
        {
            var restDot = rest.IndexOf('.');
            if (restDot < 0)
            {
                if (int.TryParse(rest, out var index) && index >= 0 && index < list.Count)
                {
                    list[index] = null;
                    return true;
                }
                return false;
            }
            var indexStr = rest[..restDot];
            var innerPath = rest[(restDot + 1)..];
            if (int.TryParse(indexStr, out var arrayIndex) && arrayIndex >= 0 && arrayIndex < list.Count && list[arrayIndex] is IDictionary<string, object?> innerDict)
                return RemovePathFromInput(innerDict, innerPath);
        }
        return false;
    }

    /// <summary>
    /// Converts an input path (may contain array indices, e.g. "Courses.0.Description") to a schema path (e.g. "Courses.Description").
    /// </summary>
    public static string ToSchemaPath(string inputPath)
    {
        if (string.IsNullOrEmpty(inputPath)) return inputPath;
        var segments = inputPath.Split('.');
        var schemaSegments = segments.Where(s => !string.IsNullOrEmpty(s) && !int.TryParse(s, out _)).ToArray();
        return string.Join(".", schemaSegments);
    }

    /// <summary>
    /// Resolves field definition for a path (root or nested), max 5 levels.
    /// Path may contain array indices (e.g. "Courses.0.Description"); they are skipped when resolving against schema.
    /// </summary>
    public static FieldDefinitionResponse? GetFieldDefForPath(SchemaDefinitionExtended schema, string path)
    {
        if (string.IsNullOrEmpty(path) || path == PolicyConstants.DbEntityIdFieldName)
            return null;

        var schemaPath = ToSchemaPath(path);
        if (string.IsNullOrEmpty(schemaPath)) return null;

        var exact = schema.Fields.FirstOrDefault(f => f.Name == schemaPath);
        if (exact != null) return exact;

        var segments = schemaPath.Split('.');
        if (segments.Length == 0 || segments.Length > PolicyConstants.MaxNestedLevelIterationLimit)
            return null;

        List<FieldDefinitionResponse>? currentLevel = schema.Fields;
        FieldDefinitionResponse? matched = null;

        for (var i = 0; i < segments.Length && currentLevel != null; i++)
        {
            var segment = segments[i];
            if (string.IsNullOrEmpty(segment)) continue;
            matched = currentLevel.FirstOrDefault(f => string.Equals(f.Name, segment, StringComparison.Ordinal));
            if (matched == null) return null;
            currentLevel = i < segments.Length - 1 ? matched.Fields : null;
        }

        return matched;
    }

    /// <summary>
    /// Returns true if the field should be excluded by CLS (custom with a CLS policy that does not grant access).
    /// When there are no CLS policies for this path/operation, do not exclude (e.g. schema uses only RLS for Custom).
    /// </summary>
    public static bool ShouldExcludeFieldByCls(AccessPrincipal principal, SchemaDefinitionExtended schema, PolicyOperation operation, string fieldPath, SchemaAccessLevel schemaAccessLevel)
    {
        var applicablePolicies = schema.Policies
            .Where(p => (p.Operation == operation || p.Operation == PolicyOperation.ALL) && p.PolicyType == PolicyType.CLS && p.CoversPath(fieldPath))
            .ToList();
        if (applicablePolicies.Count == 0)
            return false;
        return applicablePolicies.All(p => !p.EvaluatePolicy(principal).IsAccessGranted);
    }

    /// <summary>
    /// Gets schema access level for the given operation (Write/Edit/Delete/Read).
    /// </summary>
    public static SchemaAccessLevel GetSchemaAccessLevelForOperation(SchemaDefinitionExtended schema, PolicyOperation operation) =>
        operation == PolicyOperation.WRITE ? schema.WriteAccessLevel
            : operation == PolicyOperation.EDIT ? schema.EditAccessLevel
            : operation == PolicyOperation.DELETE ? schema.DeleteAccessLevel
            : schema.ReadAccessLevel;

    /// <summary>
    /// Evaluates CLS policies for mutation input paths and returns excluded fields.
    /// </summary>
    public static PolicyEvaluationResult EvaluateClsPoliciesForInput(AccessPrincipal principal, SchemaDefinitionExtended schema, PolicyOperation operation, List<string> fieldPaths)
    {
        if (principal.IsRootTenant)
            return new PolicyEvaluationResult { IsAccessGranted = true };

        var schemaAccessLevel = GetSchemaAccessLevelForOperation(schema, operation);
        var result = new PolicyEvaluationResult { IsAccessGranted = false };

        foreach (var path in fieldPaths.Distinct())
        {
            var fieldDef = GetFieldDefForPath(schema, path);
            if (fieldDef is null)
            {
                if (path != PolicyConstants.DbEntityIdFieldName && !path.Contains('.'))
                    result.ExcludedFields.Add(path);
                continue;
            }
            var rawFieldLevel = operation == PolicyOperation.WRITE ? fieldDef.WriteAccessLevel : fieldDef.EditAccessLevel;
            var fieldAccessLevel = rawFieldLevel == SchemaAccessLevel.Inherited ? schemaAccessLevel : rawFieldLevel;
            if (fieldAccessLevel == SchemaAccessLevel.Public) continue;
            if (fieldAccessLevel == SchemaAccessLevel.User)
            {
                var isAuthenticated = principal.IsAuthenticated;
                if (!isAuthenticated)
                    result.ExcludedFields.Add(path);
                continue;
            }
            if (fieldAccessLevel == SchemaAccessLevel.Custom && ShouldExcludeFieldByCls(principal, schema, operation, path, schemaAccessLevel))
                result.ExcludedFields.Add(path);
        }
        return result;
    }
    /// <summary>
    /// The written paths the caller may not write, for a write that is refused instead of stripped. Same levels as
    /// <see cref="EvaluateClsPoliciesForInput"/> (Public, User, Custom with column policies), but a column rule that
    /// reads a schema field is checked against <paramref name="dataRow"/>: the stored row on EDIT, the new row on WRITE.
    /// Without a row such a rule passes on its token part alone, as before. Paths the schema does not define are not
    /// returned here (see <see cref="UnknownRootPaths"/>).
    /// </summary>
    public static List<string> FindDeniedPaths(AccessPrincipal principal, SchemaDefinitionExtended schema, PolicyOperation operation, IEnumerable<string> fieldPaths, BsonDocument? dataRow)
    {
        var denied = new List<string>();
        if (principal.IsRootTenant)
            return denied;

        var schemaAccessLevel = GetSchemaAccessLevelForOperation(schema, operation);
        foreach (var path in fieldPaths.Distinct(StringComparer.Ordinal))
        {
            var fieldDef = GetFieldDefForPath(schema, path);
            if (fieldDef is not null && !MayWriteField(principal, schema, operation, path, fieldDef, schemaAccessLevel, dataRow))
                denied.Add(path);
        }
        return denied;
    }

    /// <summary>Root paths the schema does not define (never <c>_id</c>): the input APIs drop them, as before.</summary>
    public static List<string> UnknownRootPaths(SchemaDefinitionExtended schema, IEnumerable<string> fieldPaths) =>
        fieldPaths
            .Where(p => p != PolicyConstants.DbEntityIdFieldName && !p.Contains('.') && GetFieldDefForPath(schema, p) is null)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static bool MayWriteField(AccessPrincipal principal, SchemaDefinitionExtended schema, PolicyOperation operation, string path,
        FieldDefinitionResponse fieldDef, SchemaAccessLevel schemaAccessLevel, BsonDocument? dataRow)
    {
        var rawFieldLevel = operation == PolicyOperation.WRITE ? fieldDef.WriteAccessLevel : fieldDef.EditAccessLevel;
        var fieldAccessLevel = rawFieldLevel == SchemaAccessLevel.Inherited ? schemaAccessLevel : rawFieldLevel;
        return fieldAccessLevel switch
        {
            SchemaAccessLevel.User => principal.IsAuthenticated,
            SchemaAccessLevel.Custom => ColumnPoliciesGrant(principal, schema, operation, path, dataRow),
            _ => true
        };
    }

    // Same choice of policies as ShouldExcludeFieldByCls: none covering the path = allowed; otherwise one must grant.
    private static bool ColumnPoliciesGrant(AccessPrincipal principal, SchemaDefinitionExtended schema, PolicyOperation operation, string path, BsonDocument? dataRow)
    {
        var applicable = schema.Policies
            .Where(p => (p.Operation == operation || p.Operation == PolicyOperation.ALL) && p.PolicyType == PolicyType.CLS && p.CoversPath(path))
            .ToList();
        if (applicable.Count == 0)
            return true;

        return applicable.Any(policy =>
        {
            var result = policy.EvaluatePolicy(principal);
            if (!result.IsAccessGranted)
                return false;
            if (dataRow is null || !result.RequiresDataFilter || result.DataFilter.ElementCount == 0)
                return true;
            return BsonFilterMatcher.Matches(dataRow, result.DataFilter);
        });
    }
}
