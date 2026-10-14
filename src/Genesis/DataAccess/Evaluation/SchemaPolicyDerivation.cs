using Blocks.Genesis.DataAccess;
using Blocks.Genesis.Entities.Data;

namespace Blocks.Genesis.DataAccess.Evaluation;

/// <summary>
/// Derives the per-field policies a schema is evaluated with: fields left at Inherited get a column
/// copy of each row policy, and column policies the access levels make irrelevant are dropped. Former
/// private methods of the gateway's GraphqlSchemaBuilder, moved unchanged; the builder calls them here.
/// The only difference is that the in-memory clones are stamped from <c>stampedBy</c> instead of the
/// ambient BlocksContext (former DefaultValueInjection.InjectDefaultValue); that stamp is never evaluated.
/// </summary>
public static class SchemaPolicyDerivation
{
    public static void SetFieldsPolicies(SchemaDefinitionExtended schema, List<FieldDefinitionResponse> fields, IEnumerable<DataAccessPolicy> rlsSchemaPolicies, string schemaId, string parentFieldName, AccessPrincipal? stampedBy)
    {
        foreach (var field in fields)
        {
            var fullPath = string.IsNullOrEmpty(parentFieldName) ? field.Name : parentFieldName + "." + field.Name;
            if (!SchemaTypeHelper.IsScalar(field.Type))
            {
                SetFieldsPolicies(schema, field.Fields, rlsSchemaPolicies, schemaId, fullPath, stampedBy);
                continue;
            }
            if (field.ReadAccessLevel == SchemaAccessLevel.Inherited)
            {
                var rlsForRead = rlsSchemaPolicies.Where(p => p.Operation == PolicyOperation.READ || p.Operation == PolicyOperation.ALL);
                AddInheritedClsPoliciesToSchema(schema, rlsForRead, fullPath, PolicyOperation.READ, stampedBy);
            }
            if (field.WriteAccessLevel == SchemaAccessLevel.Inherited)
            {
                var rlsForWrite = rlsSchemaPolicies.Where(p => p.Operation == PolicyOperation.WRITE || p.Operation == PolicyOperation.ALL);
                AddInheritedClsPoliciesToSchema(schema, rlsForWrite, fullPath, PolicyOperation.WRITE, stampedBy);
            }
            if (field.EditAccessLevel == SchemaAccessLevel.Inherited)
            {
                var rlsForEdit = rlsSchemaPolicies.Where(p => p.Operation == PolicyOperation.EDIT || p.Operation == PolicyOperation.ALL);
                AddInheritedClsPoliciesToSchema(schema, rlsForEdit, fullPath, PolicyOperation.EDIT, stampedBy);
            }
            if (field.DeleteAccessLevel == SchemaAccessLevel.Inherited)
            {
                var rlsForDelete = rlsSchemaPolicies.Where(p => p.Operation == PolicyOperation.DELETE || p.Operation == PolicyOperation.ALL);
                AddInheritedClsPoliciesToSchema(schema, rlsForDelete, fullPath, PolicyOperation.DELETE, stampedBy);
            }
        }
    }

    public static void CleanupClsPoliciesBaseOnSchemaAccessLevel(SchemaDefinitionExtended schema)
    {
        var clsPolicies = schema.Policies.Where(p => p.PolicyType == PolicyType.CLS).ToList();
        foreach (var policy in clsPolicies)
        {
            foreach (var fieldPath in policy.FieldNames)
            {
                var fieldDef = ClsInputHelper.GetFieldDefForPath(schema, fieldPath);
                if (fieldDef == null) continue;

                var effectiveRead = fieldDef.ReadAccessLevel == SchemaAccessLevel.Inherited ? schema.ReadAccessLevel : fieldDef.ReadAccessLevel;
                var effectiveWrite = fieldDef.WriteAccessLevel == SchemaAccessLevel.Inherited ? schema.WriteAccessLevel : fieldDef.WriteAccessLevel;
                var effectiveEdit = fieldDef.EditAccessLevel == SchemaAccessLevel.Inherited ? schema.EditAccessLevel : fieldDef.EditAccessLevel;
                var effectiveDelete = fieldDef.DeleteAccessLevel == SchemaAccessLevel.Inherited ? schema.DeleteAccessLevel : fieldDef.DeleteAccessLevel;

                if (policy.Operation == PolicyOperation.READ && IgnoreClsPolicy(schema.ReadAccessLevel, effectiveRead))
                {
                    RemoveClsPolicy(schema, policy, fieldPath);
                }
                else if (policy.Operation == PolicyOperation.WRITE && IgnoreClsPolicy(schema.WriteAccessLevel, effectiveWrite))
                {
                    RemoveClsPolicy(schema, policy, fieldPath);
                }
                else if (policy.Operation == PolicyOperation.EDIT && IgnoreClsPolicy(schema.EditAccessLevel, effectiveEdit))
                {
                    RemoveClsPolicy(schema, policy, fieldPath);
                }
                else if (policy.Operation == PolicyOperation.DELETE && IgnoreClsPolicy(schema.DeleteAccessLevel, effectiveDelete))
                {
                    RemoveClsPolicy(schema, policy, fieldPath);
                }
            }
        }
    }
    public static bool IgnoreClsPolicy(SchemaAccessLevel schemaAccessLevel, SchemaAccessLevel fieldAccessLevel)
    {
        if (fieldAccessLevel == SchemaAccessLevel.Public || fieldAccessLevel == SchemaAccessLevel.User)
        {
            return true;
        }
        if (fieldAccessLevel == SchemaAccessLevel.Inherited && schemaAccessLevel != SchemaAccessLevel.Custom)
        {
            return true;
        }
        return false;
    }

    public static void RemoveClsPolicy(SchemaDefinitionExtended schema, DataAccessPolicy clsPolicy, string fieldName)
    {
        if (clsPolicy.FieldNames.Length > 1)
        {
            var policy = schema.Policies.FirstOrDefault(p => p.ItemId == clsPolicy.ItemId);
            if (policy is null)
            {
                return;
            }
            policy.FieldNames = policy.FieldNames.Where(f => f != fieldName).ToArray();
        }
        else
        {
            schema.Policies.RemoveAll(p => p.ItemId == clsPolicy.ItemId);
        }
    }

    public static void AddInheritedClsPoliciesToSchema(SchemaDefinitionExtended schema,
        IEnumerable<DataAccessPolicy> rlsSchemaPolicies,
        string fieldPath,
        PolicyOperation operation,
        AccessPrincipal? stampedBy)
    {
        if (operation == PolicyOperation.READ && (schema.ReadAccessLevel == SchemaAccessLevel.Public || schema.ReadAccessLevel == SchemaAccessLevel.User))
        { return; }
        if (operation == PolicyOperation.WRITE && (schema.WriteAccessLevel == SchemaAccessLevel.Public || schema.WriteAccessLevel == SchemaAccessLevel.User))
        { return; }
        if (operation == PolicyOperation.EDIT && (schema.EditAccessLevel == SchemaAccessLevel.Public || schema.EditAccessLevel == SchemaAccessLevel.User))
        { return; }
        if (operation == PolicyOperation.DELETE && (schema.DeleteAccessLevel == SchemaAccessLevel.Public || schema.DeleteAccessLevel == SchemaAccessLevel.User))
        { return; }
        var clsPolicies = rlsSchemaPolicies.Select(p => p.Clone()).ToList();
        clsPolicies.ForEach(p =>
        {
            p.FieldNames = [fieldPath];
            p.Operation = operation;
            p.PolicyType = PolicyType.CLS;
            StampDefaults(p, stampedBy);
        });
        schema.Policies.RemoveAll(p => p.SchemaId == schema.ItemId && p.PolicyType == PolicyType.CLS && p.Operation == operation && p.FieldNames.Contains(fieldPath));
        schema.Policies.AddRange(clsPolicies);
    }

    // Former DefaultValueInjection.InjectDefaultValue, with BlocksContext.GetContext() replaced by the principal.
    private static void StampDefaults(GraphQlBaseEntity entity, AccessPrincipal? by)
    {
        var isInsertOperation = false;

        if (string.IsNullOrWhiteSpace(entity.ItemId))
        {
            entity.ItemId = Guid.NewGuid().ToString();
            isInsertOperation = true;
        }

        if (entity.CreatedDate == default)
        {
            entity.CreatedDate = DateTime.UtcNow;
        }

        entity.LastUpdatedDate = DateTime.UtcNow;

        if (by is { HasContext: true })
        {
            entity.CreatedBy = string.IsNullOrWhiteSpace(entity.CreatedBy) ? by.UserId : entity.CreatedBy;
            entity.LastUpdatedBy = by.UserId;
            entity.OrganizationId = isInsertOperation ? by.OrganizationId : entity.OrganizationId;
        }
    }
}
