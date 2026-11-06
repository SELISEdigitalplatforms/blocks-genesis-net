using Blocks.Genesis.DataAccess.Rules;
using Blocks.Genesis.Entities.Data;

namespace XUnitTest.DataAccess.Rules;

public class ReferenceRuleCompatibilityTests
{
    private static readonly FieldDefinition Tags = new() { Name = "Tags", Type = "String", IsArray = true };

    private static SchemaDefinition Role(bool system) => new()
    {
        SchemaName = "Role", SchemaType = SchemaType.Entity, IsSystemEntity = system,
        Fields = [new FieldDefinition { Name = "Slug", Type = "String" }],
    };

    [Fact]
    public void TenantRules_PointAtTenantOrSystemEntities_SystemRulesAtSystemEntities()
    {
        // Changed 2026-10-25 (owner decision, validation and access rules plan, section 8): a tenant rule may also
        // point at a system entity. A system rule still points at system entities only.
        Assert.Null(ReferenceRuleCompatibility.Check(Tags, Role(system: false), "Slug"));
        Assert.Null(ReferenceRuleCompatibility.Check(Tags, Role(system: true), "Slug"));
        Assert.Equal(ReferenceRuleCompatibility.TargetFieldMustExist, ReferenceRuleCompatibility.Check(Tags, Role(system: true), "Missing"));

        Assert.Null(ReferenceRuleCompatibility.Check(Tags, Role(system: true), "Slug", systemSchemas: true));
        Assert.Equal(ReferenceRuleCompatibility.TargetSchemaMustBeEntity, ReferenceRuleCompatibility.Check(Tags, Role(system: false), "Slug", systemSchemas: true));
        Assert.Equal(ReferenceRuleCompatibility.TargetFieldMustExist, ReferenceRuleCompatibility.Check(Tags, Role(system: true), "Missing", systemSchemas: true));
    }
}
