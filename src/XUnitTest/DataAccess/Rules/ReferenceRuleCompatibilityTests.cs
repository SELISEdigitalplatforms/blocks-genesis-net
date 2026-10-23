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
    public void TenantRules_PointAtTenantEntities_SystemRulesAtSystemEntities()
    {
        Assert.Null(ReferenceRuleCompatibility.Check(Tags, Role(system: false), "Slug"));
        Assert.Equal(ReferenceRuleCompatibility.TargetSchemaMustBeEntity, ReferenceRuleCompatibility.Check(Tags, Role(system: true), "Slug"));

        Assert.Null(ReferenceRuleCompatibility.Check(Tags, Role(system: true), "Slug", systemSchemas: true));
        Assert.Equal(ReferenceRuleCompatibility.TargetSchemaMustBeEntity, ReferenceRuleCompatibility.Check(Tags, Role(system: false), "Slug", systemSchemas: true));
        Assert.Equal(ReferenceRuleCompatibility.TargetFieldMustExist, ReferenceRuleCompatibility.Check(Tags, Role(system: true), "Missing", systemSchemas: true));
    }
}
