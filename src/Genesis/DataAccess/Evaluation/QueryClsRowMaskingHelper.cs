using Blocks.Genesis.DataAccess;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;

namespace Blocks.Genesis.DataAccess.Evaluation;

/// <summary>
/// Helper for applying Column-Level Security (CLS) masking to query result rows.
/// Nulls or removes fields based on schema access levels and policy evaluation.
/// </summary>
public static class QueryClsRowMaskingHelper
{
    /// <summary>
    /// Gets the effective read access level for a path: Inherited resolves to schema level.
    /// </summary>
    public static SchemaAccessLevel GetEffectiveReadAccessLevel(SchemaDefinitionExtended schema, string path)
    {
        var fieldDef = ClsInputHelper.GetFieldDefForPath(schema, path);
        if (fieldDef == null) return schema.ReadAccessLevel;
        return fieldDef.ReadAccessLevel == SchemaAccessLevel.Inherited ? schema.ReadAccessLevel : fieldDef.ReadAccessLevel;
    }

    /// <summary>
    /// Collects all field paths (root and nested) with their effective read access level.
    /// </summary>
    public static List<(string Path, SchemaAccessLevel EffectiveReadLevel)> GetPathsWithEffectiveReadAccessLevel(
        SchemaDefinitionExtended schema)
    {
        var result = new List<(string, SchemaAccessLevel)>();
        CollectPathsWithEffectiveLevel(schema.Fields, schema.ReadAccessLevel, "", result);
        return result;
    }

    private static void CollectPathsWithEffectiveLevel(
        List<FieldDefinitionResponse>? fields,
        SchemaAccessLevel schemaReadLevel,
        string prefix,
        List<(string Path, SchemaAccessLevel EffectiveReadLevel)> result)
    {
        if (fields == null || fields.Count == 0) return;
        foreach (var field in fields)
        {
            var path = string.IsNullOrEmpty(prefix) ? field.Name : prefix + "." + field.Name;
            var effective = field.ReadAccessLevel == SchemaAccessLevel.Inherited ? schemaReadLevel : field.ReadAccessLevel;
            result.Add((path, effective));
            if (field.Fields != null && field.Fields.Count > 0)
                CollectPathsWithEffectiveLevel(field.Fields, schemaReadLevel, path, result);
        }
    }
    private static readonly HashSet<string> SystemFieldNames = new()
    {
        PolicyConstants.DbEntityIdFieldName,
        nameof(GraphQlBaseEntity.ItemId)
    };

    /// <summary>
    /// Masks a row by CLS policies: evaluates allowed fields, nulls denied paths,
    /// removes evaluation-only fields, and applies custom/user access level masks.
    /// </summary>
    public static Dictionary<string, object> MaskRowByClsPolicies(
        AccessPrincipal principal,
        Dictionary<string, object> row,
        SchemaDefinitionExtended schema,
        List<DataAccessPolicy> clsPolicies,
        HashSet<string> evaluationOnlyFieldPaths,
        Func<DataAccessPolicy, IDictionary<string, object>, bool> rowSatisfiesPolicy)
    {
        var fieldNamesProtectedByCls = clsPolicies.Count > 0
            ? clsPolicies.SelectMany(p => p.FieldNames).Distinct().ToHashSet()
            : new HashSet<string>();

        if (clsPolicies.Count == 0)
        {
            ApplyCustomSchemaMaskWhenNoCls(principal, row, schema);
        }
        else
        {
            var allowedFieldNames = ComputeAllowedFieldNamesForRow(
                row, schema, clsPolicies, fieldNamesProtectedByCls, rowSatisfiesPolicy);
            NullDeniedFieldsByPath(row, allowedFieldNames, fieldNamesProtectedByCls, prefix: "");
            RemoveFieldsByPaths(row, evaluationOnlyFieldPaths);
        }

        MaskCustomFieldsWithNoPolicy(row, schema, fieldNamesProtectedByCls);
        MaskUserOnlyFieldsWhenUnauthenticated(principal, row, schema);
        return row;
    }

    /// <summary>
    /// Nulls fields (root and nested) that have effective ReadAccessLevel == Custom but no CLS policy covering them.
    /// </summary>
    public static void MaskCustomFieldsWithNoPolicy(
        Dictionary<string, object> row,
        SchemaDefinitionExtended schema,
        HashSet<string> fieldNamesCoveredByClsPolicies)
    {
        // No listed CLS columns means policies do not scope column-level access; do not strip Custom fields.
        if (fieldNamesCoveredByClsPolicies == null || fieldNamesCoveredByClsPolicies.Count == 0)
            return;

        var pathsWithLevel = GetPathsWithEffectiveReadAccessLevel(schema);
        var customPathsWithNoPolicy = pathsWithLevel
            .Where(t => t.EffectiveReadLevel == SchemaAccessLevel.Custom &&
                        !DataAccessPolicyHelper.IsPathCoveredByClsFieldNames(t.Path, fieldNamesCoveredByClsPolicies))
            .Select(t => t.Path)
            .ToHashSet();

        if (customPathsWithNoPolicy.Count == 0) return;
        NullPathsInRow(row, customPathsWithNoPolicy, prefix: "");
    }

    /// <summary>
    /// Nulls fields with effective ReadAccessLevel == User when the request is unauthenticated.
    /// </summary>
    public static void MaskUserOnlyFieldsWhenUnauthenticated(
        AccessPrincipal principal,
        Dictionary<string, object> row,
        SchemaDefinitionExtended schema)
    {
        if (IsRequestAuthenticated(principal)) return;

        var pathsWithLevel = GetPathsWithEffectiveReadAccessLevel(schema);
        var userOnlyPaths = pathsWithLevel
            .Where(t => t.EffectiveReadLevel == SchemaAccessLevel.User)
            .Select(t => t.Path)
            .ToHashSet();
        if (userOnlyPaths.Count == 0) return;
        NullPathsInRow(row, userOnlyPaths, prefix: "");
    }

    /// <summary>
    /// Collects field paths from the schema tree where the predicate is true, up to maxDepth levels.
    /// </summary>
    public static HashSet<string> GetPathsFromSchemaFieldsByCondition(
        List<FieldDefinitionResponse> fields,
        Func<FieldDefinitionResponse, bool> predicate,
        int maxDepth,
        string prefix)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (fields == null || fields.Count == 0 || maxDepth <= 0) return paths;

        foreach (var field in fields)
        {
            var path = string.IsNullOrEmpty(prefix) ? field.Name : prefix + "." + field.Name;
            if (predicate(field))
                paths.Add(path);
            if (maxDepth > 1 && field.Fields != null && field.Fields.Count > 0)
                paths.UnionWith(GetPathsFromSchemaFieldsByCondition(field.Fields, predicate, maxDepth - 1, path));
        }
        return paths;
    }

    /// <summary>
    /// Recursively nulls every path in pathsToNull that appears in the row (schema paths; row paths may include array indices).
    /// </summary>
    public static void NullPathsInRow(IDictionary<string, object> row, HashSet<string> pathsToNull, string prefix)
    {
        var keys = row.Keys.ToList();
        foreach (var key in keys)
        {
            var path = string.IsNullOrEmpty(prefix) ? key : prefix + "." + key;
            if (pathsToNull.Contains(path) || pathsToNull.Any(p => DataAccessPolicyHelper.RowPathMatchesSchemaPath(p, path)))
            {
                row[key] = null!;
                continue;
            }
            var value = row[key];
            if (value is IDictionary<string, object> nested)
                NullPathsInRow(nested, pathsToNull, path);
            else if (value is BsonDocument bson)
                NullPathsInRowBson(bson, pathsToNull, path);
            else if (value is BsonArray bsonArr)
                NullPathsInRowBsonArray(bsonArr, pathsToNull, path);
            else if (value is IList<object> list && !(value is string))
                NullPathsInRowList(list, pathsToNull, path);
        }
    }

    private static void NullPathsInRowList(IList<object> list, HashSet<string> pathsToNull, string path)
    {
        for (var i = 0; i < list.Count; i++)
        {
            var itemPath = path + "." + i;
            if (pathsToNull.Contains(itemPath) || pathsToNull.Any(p => DataAccessPolicyHelper.RowPathMatchesSchemaPath(p, itemPath)))
            {
                list[i] = null!;
                continue;
            }
            var item = list[i];
            if (item is IDictionary<string, object> itemDict)
                NullPathsInRow(itemDict, pathsToNull, itemPath);
            else if (item is BsonDocument itemBson)
                NullPathsInRowBson(itemBson, pathsToNull, itemPath);
        }
    }

    private static void NullPathsInRowBson(BsonDocument doc, HashSet<string> pathsToNull, string prefix)
    {
        var names = doc.Names.ToList();
        foreach (var name in names)
        {
            var path = string.IsNullOrEmpty(prefix) ? name : prefix + "." + name;
            if (pathsToNull.Contains(path) || pathsToNull.Any(p => DataAccessPolicyHelper.RowPathMatchesSchemaPath(p, path)))
            {
                doc.Remove(name);
                continue;
            }
            var value = doc[name];
            if (value?.IsBsonDocument == true)
                NullPathsInRowBson(value.AsBsonDocument, pathsToNull, path);
            else if (value?.IsBsonArray == true)
                NullPathsInRowBsonArray(value.AsBsonArray, pathsToNull, path);
        }
    }

    private static void NullPathsInRowBsonArray(BsonArray arr, HashSet<string> pathsToNull, string prefix)
    {
        for (var i = 0; i < arr.Count; i++)
        {
            var itemPath = prefix + "." + i;
            if (pathsToNull.Contains(itemPath) || pathsToNull.Any(p => DataAccessPolicyHelper.RowPathMatchesSchemaPath(p, itemPath)))
            {
                arr[i] = BsonNull.Value;
                continue;
            }
            var item = arr[i];
            if (item?.IsBsonDocument == true)
                NullPathsInRowBson(item.AsBsonDocument, pathsToNull, itemPath);
        }
    }

    public static bool IsRequestAuthenticated(AccessPrincipal principal) =>
        principal?.IsAuthenticated == true;

    /// <summary>
    /// When schema has Custom read access and no CLS policies, removes all non-system keys from the row.
    /// </summary>
    public static Dictionary<string, object> ApplyCustomSchemaMaskWhenNoCls(
        AccessPrincipal principal,
        Dictionary<string, object> row,
        SchemaDefinitionExtended schema)
    {
        if (schema.ReadAccessLevel != SchemaAccessLevel.Custom || principal.IsRootTenant)
            return row;

        var nonSystemKeys = row.Keys.Where(k => !SystemFieldNames.Contains(k)).ToList();
        foreach (var key in nonSystemKeys)
            row.Remove(key);
        return row;
    }

    /// <summary>
    /// Computes the set of field paths allowed for this row based on CLS policies.
    /// </summary>
    public static HashSet<string> ComputeAllowedFieldNamesForRow(
        Dictionary<string, object> row,
        SchemaDefinitionExtended schema,
        List<DataAccessPolicy> clsPolicies,
        HashSet<string> fieldNamesProtectedByCls,
        Func<DataAccessPolicy, IDictionary<string, object>, bool> rowSatisfiesPolicy)
    {
        var pathsWithLevel = GetPathsWithEffectiveReadAccessLevel(schema);
        // When no CLS field names are configured, nothing is column-protected; do not treat Custom paths as disallowed.
        var customPathsWithNoPolicy = fieldNamesProtectedByCls.Count == 0
            ? new HashSet<string>()
            : pathsWithLevel
                .Where(t => t.EffectiveReadLevel == SchemaAccessLevel.Custom &&
                            !DataAccessPolicyHelper.IsPathCoveredByClsFieldNames(t.Path, fieldNamesProtectedByCls))
                .Select(t => t.Path)
                .ToHashSet();

        var allowed = new HashSet<string>(SystemFieldNames);
        foreach (var key in row.Keys)
        {
            if (customPathsWithNoPolicy.Contains(key))
                continue;
            if (!DataAccessPolicyHelper.IsPathProtectedByCls(key, fieldNamesProtectedByCls))
                allowed.Add(key);
        }
        foreach (var policy in clsPolicies)
        {
            if (!rowSatisfiesPolicy(policy, row)) continue;
            foreach (var fieldName in policy.FieldNames)
                allowed.Add(fieldName);
        }
        return allowed;
    }

    /// <summary>
    /// Recursively nulls denied fields by path (protected by CLS but not in allowed set).
    /// </summary>
    public static void NullDeniedFieldsByPath(
        IDictionary<string, object> row,
        HashSet<string> allowedFieldNames,
        HashSet<string> fieldNamesProtectedByCls,
        string prefix)
    {
        var keys = row.Keys.ToList();
        foreach (var key in keys)
        {
            var path = string.IsNullOrEmpty(prefix) ? key : prefix + "." + key;
            var isProtected = IsPathDirectlyProtectedByCls(path, fieldNamesProtectedByCls);
            var isAllowed = DataAccessPolicyHelper.IsPathAllowed(path, allowedFieldNames, fieldNamesProtectedByCls);
            if (isProtected && !isAllowed)
            {
                row[key] = null!;
                continue;
            }
            var value = row[key];
            if (value is IDictionary<string, object> nested)
                NullDeniedFieldsByPath(nested, allowedFieldNames, fieldNamesProtectedByCls, path);
            else if (value is BsonDocument bson)
                NullDeniedFieldsByPathBson(bson, allowedFieldNames, fieldNamesProtectedByCls, path);
            else if (value is BsonArray bsonArr)
                NullDeniedFieldsByPathBsonArray(bsonArr, allowedFieldNames, fieldNamesProtectedByCls, path);
            else if (value is IList<object> list && !(value is string))
                NullDeniedFieldsByPathList(list, allowedFieldNames, fieldNamesProtectedByCls, path);
        }
    }

    private static void NullDeniedFieldsByPathList(
        IList<object> list,
        HashSet<string> allowedFieldNames,
        HashSet<string> fieldNamesProtectedByCls,
        string path)
    {
        for (var i = 0; i < list.Count; i++)
        {
            var itemPath = path + "." + i;
            var isItemProtected = IsPathDirectlyProtectedByCls(itemPath, fieldNamesProtectedByCls);
            var isItemAllowed = DataAccessPolicyHelper.IsPathAllowed(itemPath, allowedFieldNames, fieldNamesProtectedByCls);
            if (isItemProtected && !isItemAllowed)
            {
                list[i] = null!;
                continue;
            }
            var item = list[i];
            if (item is IDictionary<string, object> itemDict)
                NullDeniedFieldsByPath(itemDict, allowedFieldNames, fieldNamesProtectedByCls, itemPath);
            else if (item is BsonDocument itemBson)
                NullDeniedFieldsByPathBson(itemBson, allowedFieldNames, fieldNamesProtectedByCls, itemPath);
        }
    }

    private static void NullDeniedFieldsByPathBsonArray(
        BsonArray arr,
        HashSet<string> allowedFieldNames,
        HashSet<string> fieldNamesProtectedByCls,
        string prefix)
    {
        for (var i = 0; i < arr.Count; i++)
        {
            var itemPath = prefix + "." + i;
            var isProtected = IsPathDirectlyProtectedByCls(itemPath, fieldNamesProtectedByCls);
            var isAllowed = DataAccessPolicyHelper.IsPathAllowed(itemPath, allowedFieldNames, fieldNamesProtectedByCls);
            if (isProtected && !isAllowed)
            {
                arr[i] = BsonNull.Value;
                continue;
            }
            var item = arr[i];
            if (item?.IsBsonDocument == true)
                NullDeniedFieldsByPathBson(item.AsBsonDocument, allowedFieldNames, fieldNamesProtectedByCls, itemPath);
        }
    }

    private static void NullDeniedFieldsByPathBson(
        BsonDocument doc,
        HashSet<string> allowedFieldNames,
        HashSet<string> fieldNamesProtectedByCls,
        string prefix)
    {
        var names = doc.Names.ToList();
        foreach (var name in names)
        {
            var path = string.IsNullOrEmpty(prefix) ? name : prefix + "." + name;
            var isProtected = IsPathDirectlyProtectedByCls(path, fieldNamesProtectedByCls);
            var isAllowed = DataAccessPolicyHelper.IsPathAllowed(path, allowedFieldNames, fieldNamesProtectedByCls);
            if (isProtected && !isAllowed)
            {
                doc.Remove(name);
                continue;
            }
            var value = doc[name];
            if (value?.IsBsonDocument == true)
                NullDeniedFieldsByPathBson(value.AsBsonDocument, allowedFieldNames, fieldNamesProtectedByCls, path);
            else if (value?.IsBsonArray == true)
                NullDeniedFieldsByPathBsonArray(value.AsBsonArray, allowedFieldNames, fieldNamesProtectedByCls, path);
        }
    }

    private static bool IsPathDirectlyProtectedByCls(string path, HashSet<string> fieldNamesProtectedByCls)
    {
        if (string.IsNullOrEmpty(path) || fieldNamesProtectedByCls == null || fieldNamesProtectedByCls.Count == 0)
            return false;

        if (fieldNamesProtectedByCls.Contains(path))
            return true;

        if (fieldNamesProtectedByCls.Any(f => !string.IsNullOrEmpty(f) && path.StartsWith(f + ".", StringComparison.Ordinal)))
            return true;

        return fieldNamesProtectedByCls.Any(f => !string.IsNullOrEmpty(f) && DataAccessPolicyHelper.RowPathMatchesSchemaPath(f, path));
    }

    /// <summary>
    /// Removes all fields at the given dotted paths from the row.
    /// </summary>
    public static void RemoveFieldsByPaths(IDictionary<string, object> row, HashSet<string> paths)
    {
        foreach (var path in paths)
            RemoveFieldByPath(row, path);
    }

    /// <summary>
    /// Removes a single field by dotted path (e.g. "NavigationHistory.Route").
    /// </summary>
    public static void RemoveFieldByPath(IDictionary<string, object> item, string path)
    {
        var dotIndex = path.IndexOf('.');
        if (dotIndex < 0)
        {
            item.Remove(path);
            return;
        }
        var segment = path[..dotIndex];
        var rest = path[(dotIndex + 1)..];
        if (!item.TryGetValue(segment, out var segmentValue))
            return;
        if (segmentValue is IDictionary<string, object> nested)
            RemoveFieldByPath(nested, rest);
        else if (segmentValue is BsonDocument bson)
        {
            var innerDot = rest.IndexOf('.');
            if (innerDot < 0)
                bson.Remove(rest);
            else
            {
                var segmentName = rest[..innerDot];
                var restPath = rest[(innerDot + 1)..];
                if (bson.Contains(segmentName) && bson[segmentName] is BsonDocument innerDoc)
                    RemoveFieldFromBsonByPath(innerDoc, restPath);
            }
        }
    }

    private static void RemoveFieldFromBsonByPath(BsonDocument doc, string path)
    {
        var dotIndex = path.IndexOf('.');
        if (dotIndex < 0)
        {
            doc.Remove(path);
            return;
        }
        var segment = path[..dotIndex];
        var rest = path[(dotIndex + 1)..];
        if (doc.Contains(segment) && doc[segment] is BsonDocument nested)
            RemoveFieldFromBsonByPath(nested, rest);
    }
}
