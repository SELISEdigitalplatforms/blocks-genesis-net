using Blocks.Genesis.DataAccess;
using Blocks.Genesis.Entities.Data;

namespace Blocks.Genesis.DataAccess.Evaluation;

/// <summary>
/// Builds the field tree a schema is evaluated against (nested fields under non-scalar types). Former
/// SchemaDefinitionMapping.GetFieldDefinitionResponses / MapFieldToResponse and
/// DefaultValueInjection.DefaultFieldDescriptions, moved unchanged; the gateway forwards here.
/// </summary>
public static class FieldDefinitionMapper
{
    public static readonly IReadOnlyDictionary<string, string> DefaultFieldDescriptions = new Dictionary<string, string>
    {
        [nameof(GraphQlBaseEntity.ItemId)] = "Unique identifier for the record. System-generated and immutable once created.",
        [nameof(GraphQlBaseEntity.CreatedDate)] = "UTC timestamp when the record was first created. Automatically set on insert and never changed afterward.",
        [nameof(GraphQlBaseEntity.LastUpdatedDate)] = "UTC timestamp of the most recent update to the record. Automatically refreshed on every write operation.",
        [nameof(GraphQlBaseEntity.CreatedBy)] = "User ID of the person who originally created the record. Derived from the authentication context at insert time.",
        [nameof(GraphQlBaseEntity.LastUpdatedBy)] = "User ID of the person who last modified the record. Automatically updated from the authentication context on every write.",
        [nameof(GraphQlBaseEntity.OrganizationId)] = "List of organization IDs this record belongs to. Used for multi-tenancy and access-control scoping.",
        [nameof(GraphQlBaseEntity.Language)] = "Language code (e.g. 'en', 'de') that indicates the locale of the record. Used for localization and language-based content filtering.",
        [nameof(GraphQlBaseEntity.Tags)] = "List of tags associated with the record. Used for categorization, search, and filtering across queries.",
    };

    public static List<FieldDefinitionResponse> GetFieldDefinitionResponses(List<FieldDefinition> fields, List<DataAccessPolicy> policies, List<DataValidation> validations, string parentPath = "")
    {
        var fieldResponses = new List<FieldDefinitionResponse>();
        var isRoot = string.IsNullOrWhiteSpace(parentPath);

        // At root: direct fields are those with ReferenceFieldPath == ""
        // At nested level: direct children are unique first segments under parentPath
        List<string> directChildSegments;
        if (isRoot)
        {
            directChildSegments = fields
                .Where(f => !f.IsReferenceField)
                .Select(f => f.Name)
                .Distinct()
                .ToList();
        }
        else
        {
            var prefix = parentPath + ".";
            directChildSegments = fields
                .Where(f => f.Name.StartsWith(prefix))
                .Select(f =>
                {
                    var afterPrefix = f.Name.Substring(prefix.Length);
                    var nextDot = afterPrefix.IndexOf('.');
                    return nextDot >= 0 ? afterPrefix.Substring(0, nextDot) : afterPrefix;
                })
                .Distinct()
                .ToList();
        }

        foreach (var segment in directChildSegments)
        {
            var currentPath = isRoot ? segment : parentPath + "." + segment;
            var field = fields.FirstOrDefault(f => f.Name == currentPath);

            FieldDefinitionResponse fieldResponse;
            if (field != null)
            {
                fieldResponse = field.MapFieldToResponse(policies);
                if (SchemaTypeHelper.IsScalar(field.Type))
                {
                    fieldResponse.ValidationRule = validations.FirstOrDefault(v => v.FieldName == currentPath);
                }
                else
                {
                    fieldResponse.Fields = GetFieldDefinitionResponses(fields, policies, validations, currentPath);
                }
            }
            else
            {
                // Synthetic reference node (e.g. Child.L2Child with only Child.L2Child.Name and Child.L2Child.Qty)
                var descendantPrefix = currentPath + ".";
                fieldResponse = new FieldDefinitionResponse
                {
                    Name = segment,
                    Type = fields.FirstOrDefault(f =>
                        f.Name.StartsWith(descendantPrefix, StringComparison.Ordinal))?.ReferenceFieldType ?? string.Empty,
                    IsArray = false,
                    IsPIIData = false,
                    IsUniqueData = false,
                    Description = string.Empty,
                    Fields = GetFieldDefinitionResponses(fields, policies, validations, currentPath),
                    ReadAccessLevel = SchemaAccessLevel.Inherited,
                    WriteAccessLevel = SchemaAccessLevel.Inherited,
                    EditAccessLevel = SchemaAccessLevel.Inherited,
                    DeleteAccessLevel = SchemaAccessLevel.Inherited
                };
            }

            fieldResponses.Add(fieldResponse);
        }

        return fieldResponses;
    }

    public static FieldDefinitionResponse MapFieldToResponse(this FieldDefinition field, IEnumerable<DataAccessPolicy>? policies = null)
    {
        var fieldNameParts = field.Name?.Split('.') ?? [];
        var response = new FieldDefinitionResponse
        {
            Name = fieldNameParts is null || fieldNameParts.Length < 1 ? string.Empty : fieldNameParts.Last(),
            Type = field.Type,
            IsArray = field.IsArray,
            EnumValues = field.EnumValues ?? [],
            IsPIIData = field.IsPIIData,
            IsUniqueData = field.IsUniqueData,
            Description = !string.IsNullOrEmpty(field.Description)
                ? field.Description
                : DefaultFieldDescriptions.TryGetValue(field.Name, out var fallback)
                    ? fallback
                    : string.Empty,
            RequiredOn = field.RequiredOn,
            ReadAccessLevel = field.ReadAccessLevel,
            WriteAccessLevel = field.WriteAccessLevel,
            EditAccessLevel = field.EditAccessLevel,
            DeleteAccessLevel = field.DeleteAccessLevel,
            DefaultValue = field.DefaultValue
        };
        if (policies is not null)
        {
            response.TotalReadPolicies = policies.Count(p => p.PolicyType == PolicyType.CLS && p.Operation == PolicyOperation.READ && p.FieldNames.Contains(field.Name));
            response.TotalWritePolicies = policies.Count(p => p.PolicyType == PolicyType.CLS && p.Operation == PolicyOperation.WRITE && p.FieldNames.Contains(field.Name));
            response.TotalEditPolicies = policies.Count(p => p.PolicyType == PolicyType.CLS && p.Operation == PolicyOperation.EDIT && p.FieldNames.Contains(field.Name));
            response.TotalDeletePolicies = policies.Count(p => p.PolicyType == PolicyType.CLS && p.Operation == PolicyOperation.DELETE && p.FieldNames.Contains(field.Name));
        }

        return response;
    }

}
