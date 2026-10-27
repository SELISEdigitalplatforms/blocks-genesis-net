// Moved from blocks-data server/DataGateway.DomainService/Helpers/ReferenceRuleCompatibility.cs (dev 3664eb77) so the gateway and the platform services share one rules engine.
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;

namespace Blocks.Genesis.DataAccess.Rules;

/// <summary>
/// Which fields a Reference rule may connect. Shared by the validation API and schema import so both
/// accept the same rules.
/// </summary>
public static class ReferenceRuleCompatibility
{
    public const string SourceFieldMustExist = "Reference_Validation_Field_Must_Exist.";
    public const string SourceFieldNotSupported = "Reference_Validation_Field_Must_Be_Text_Or_Number.";
    public const string TargetSchemaMustBeEntity = "Reference_Validation_Target_Schema_Must_Be_An_Existing_Entity.";
    public const string TargetFieldMustExist = "Reference_Validation_Target_Field_Must_Be_A_Single_Value_Field_Of_The_Target.";
    public const string TypesMustMatch = "Reference_Validation_Field_Types_Must_Match.";
    public const string OnlyOneReference = "Only_One_Reference_Validation_Per_Field.";

    private enum Family { Text, Number }

    /// <summary>
    /// Null when a Reference rule from <paramref name="source"/> to <paramref name="targetFieldName"/> of
    /// <paramref name="target"/> is allowed; otherwise the error code. <paramref name="target"/> is null when
    /// no schema of that name exists.
    /// </summary>
    public static string? Check(FieldDefinition source, SchemaDefinition? target, string targetFieldName) =>
        Check(source, target, targetFieldName, systemSchemas: false);

    /// <summary>
    /// As <see cref="Check(FieldDefinition, SchemaDefinition?, string)"/>. A rule on a tenant schema may point at a
    /// tenant or a system Entity schema; with <paramref name="systemSchemas"/> the rule is on a system schema, whose
    /// targets must be system Entity schemas.
    /// </summary>
    public static string? Check(FieldDefinition source, SchemaDefinition? target, string targetFieldName, bool systemSchemas)
    {
        // A list of plain values is checked element by element; a list of objects has nothing to compare.
        var sourceFamily = FamilyOf(source.Type);
        if (sourceFamily is null)
            return SourceFieldNotSupported;

        if (target is null || target.IsDeleted || (systemSchemas && !target.IsSystemEntity) || target.SchemaType != SchemaType.Entity)
            return TargetSchemaMustBeEntity;

        // ItemId is the stored _id: a string on every gateway row.
        if (targetFieldName == ReferenceTargets.DefaultTargetField)
            return sourceFamily == Family.Text ? null : TypesMustMatch;

        var targetField = target.Fields.FirstOrDefault(f => f.Name == targetFieldName && !f.IsReferenceField);
        if (targetField is null || targetField.IsArray || !SchemaTypeHelper.IsScalar(targetField.Type))
            return TargetFieldMustExist;

        return FamilyOf(targetField.Type) == sourceFamily ? null : TypesMustMatch;
    }

    private static Family? FamilyOf(string type) => type switch
    {
        "String" or "ID" or SchemaTypeHelper.EnumTypeName => Family.Text,
        "Int" or "Float" => Family.Number,
        _ => null,
    };
}
