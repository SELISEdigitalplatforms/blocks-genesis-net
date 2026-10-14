using Blocks.Genesis.Entities.Data;

namespace Blocks.Genesis.DataAccess.Evaluation;

/// <summary>
/// Evaluation helpers for <see cref="DataAccessPolicy"/>. Former instance method of the blocks-data entity; it needs
/// <see cref="DataAccessPolicyHelper"/>, so it lives with the evaluator instead of on the entity.
/// </summary>
public static class DataAccessPolicyExtensions
{
    /// <summary>
    /// Returns true if this policy's FieldNames cover the given path.
    /// A path is covered if it equals a FieldName (e.g. "ContactInfo") or is a nested path under a FieldName (e.g. "ContactInfo.Email" when FieldNames contains "ContactInfo").
    /// Root properties: ["Name"] covers only "Name". Nested: ["ContactInfo"] covers "ContactInfo", "ContactInfo.Email", "ContactInfo.HomeAddress.House", etc.
    /// </summary>
    public static bool CoversPath(this DataAccessPolicy policy, string path)
    {
        if (string.IsNullOrEmpty(path) || policy.FieldNames == null || policy.FieldNames.Length == 0)
            return false;
        foreach (var fieldName in policy.FieldNames)
        {
            if (string.IsNullOrEmpty(fieldName)) continue;
            if (path == fieldName) return true;
            if (path.StartsWith(fieldName + ".", StringComparison.Ordinal)) return true;
            if (DataAccessPolicyHelper.RowPathMatchesSchemaPath(fieldName, path)) return true;
        }
        return false;
    }
}
