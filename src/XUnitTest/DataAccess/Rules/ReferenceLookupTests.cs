using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.DataAccess.Rules;
using Blocks.Genesis.Entities.Data;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace XUnitTest.DataAccess.Rules;

/// <summary>
/// The lookup a Reference or Must not exist in rule sends (validation and access rules plan, section 5, phase 4):
/// the rule's filter, the deleted-rows clause, and which rules resolve when the schema is built.
/// </summary>
public class ReferenceLookupTests
{
    private static PolicyRuleGroup Where(string field, PolicyOperator op, object? value) => new()
    {
        Rules = [new() { LeftOperand = field, Operator = op, RightSource = ConditionSource.STATIC_VALUE, StaticValue = value }],
    };

    private static ValidationRule Reference(int version = 1, PolicyRuleGroup? filter = null, string target = "Customer", string? field = null) =>
        new() { Type = ValidationType.Reference, Value = target, SecondaryValue = field, EvaluationVersion = version, TargetFilter = filter };

    private static ValidationRule NotExistsIn(PolicyRuleGroup? filter = null, int version = 0) =>
        new() { Type = ValidationType.NotExistsIn, Value = "Blocklist", SecondaryValue = "Email", EvaluationVersion = version, TargetFilter = filter };

    private static BsonDocument Render(FilterDefinition<BsonDocument> filter) =>
        filter.Render(new RenderArgs<BsonDocument>(BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(), BsonSerializer.SerializerRegistry));

    private static BsonDocument Condition(ValidationRule rule)
    {
        Assert.True(ReferenceLookup.TryCondition(rule, out var condition));
        return condition!;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AnUnfilteredReferenceRuleBelowVersion2_SendsTodaysLookup(int version)
    {
        // G4: the same Mongo filter as before this plan, so deleted rows still count.
        Assert.True(ReferenceLookup.TryCondition(Reference(version), out var condition));
        Assert.Null(condition);

        BsonValue[] values = ["c1", "c2"];
        Assert.Equal(Render(Builders<BsonDocument>.Filter.In("_id", values)), Render(ReferenceLookup.Filter("_id", values, condition)));
    }

    [Fact]
    public void AVersion2ReferenceRule_SkipsDeletedRows()
    {
        Assert.Equal(BsonDocument.Parse("{ IsDeleted: { $ne: true } }"), Condition(Reference(version: 2)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AMustNotExistInRule_SkipsDeletedRows_WhateverItsVersion(int version)
    {
        Assert.Equal(BsonDocument.Parse("{ IsDeleted: { $ne: true } }"), Condition(NotExistsIn(version: version)));
    }

    [Fact]
    public void AFilter_IsAddedToTheLookup()
    {
        var condition = Condition(Reference(version: 1, filter: Where("Status", PolicyOperator.EQUAL, "Active")));
        Assert.Contains("Status", condition.ToJson());
        Assert.DoesNotContain("IsDeleted", condition.ToJson());

        var both = Condition(Reference(version: 2, filter: Where("Status", PolicyOperator.EQUAL, "Active")));
        var parts = both["$and"].AsBsonArray;
        Assert.Equal(2, parts.Count);
        Assert.Equal(BsonDocument.Parse("{ IsDeleted: { $ne: true } }"), parts[1]);
    }

    [Fact]
    public void ItemIdInAFilter_IsStoredAsId_AtEveryLevel()
    {
        var filter = new PolicyRuleGroup
        {
            LogicalOperator = PolicyLogicalOperator.OR,
            Rules = [new() { LeftOperand = "ItemId", Operator = PolicyOperator.EQUAL, StaticValue = "c1" }],
            NestedGroups = [new() { Rules =
            [
                new() { LeftOperand = "ItemId", Operator = PolicyOperator.NOT_EQUAL, StaticValue = "c2" },
                new() { LeftOperand = "Address.ItemId", Operator = PolicyOperator.EQUAL, StaticValue = "a1" },
            ] }],
        };

        var json = Condition(Reference(filter: filter)).ToJson();

        Assert.DoesNotContain("\"ItemId\"", json);
        Assert.Contains("\"_id\"", json);
        Assert.Contains("\"Address.ItemId\"", json);   // only the record's own id is stored as _id
    }

    [Fact]
    public void ContainsInAFilter_MeansThePlainText()
    {
        var json = Condition(Reference(filter: Where("Name", PolicyOperator.CONTAIN, "a.b"))).ToJson();
        Assert.Contains(@"a\\.b", json);
    }

    [Fact]
    public void TheFilteredLookup_IsFieldInValuesAndTheCondition()
    {
        var condition = BsonDocument.Parse("{ IsDeleted: { $ne: true } }");
        BsonValue[] values = ["a"];
        Assert.Equal(BsonDocument.Parse("{ $and: [ { Email: { $in: ['a'] } }, { IsDeleted: { $ne: true } } ] }"), Render(ReferenceLookup.Filter("Email", values, condition)));
    }

    // ── Which rules resolve when the schema is built ──

    private static SchemaDefinitionExtended Entity(string name, params FieldDefinitionResponse[] fields) => new()
    {
        ItemId = $"sch-{name}", SchemaName = name, CollectionName = name + "s", SchemaType = SchemaType.Entity, Fields = [.. fields],
    };

    private static FieldDefinitionResponse Field(string name, params ValidationRule[] rules) => new()
    {
        Name = name, Type = "String",
        ValidationRule = rules.Length == 0 ? null : new DataValidation { FieldName = name, Validations = [.. rules] },
    };

    private static readonly SchemaDefinitionExtended Customer = Entity("Customer", Field("Status"), Field("Email"));
    private static readonly SchemaDefinitionExtended Blocklist = Entity("Blocklist", Field("Email"), Field("Active"));

    [Fact]
    public void MustNotExistIn_ResolvesLikeReference()
    {
        var rule = NotExistsIn();
        var invoice = Entity("Invoice", Field("Email", rule));

        var targets = ReferenceTargets.Build([invoice, Blocklist], strict: true, NullLogger.Instance);

        Assert.Equal(new ReferenceTarget("Blocklist", "Email", "Blocklists", "Email"), targets.Find(rule));
        Assert.True(targets.HasReferences(invoice));
        Assert.Equal(["Blocklist"], WriteRules.ReferencedSchemaNames(invoice.Fields));
    }

    [Fact]
    public void AFilterOnAPropertyTheTargetDoesNotHave_FailsAPublish()
    {
        var invoice = Entity("Invoice", Field("CustomerId", Reference(filter: Where("Missing", PolicyOperator.EQUAL, "x"))));

        var error = Assert.Throws<InvalidOperationException>(() => ReferenceTargets.Build([invoice, Customer], strict: true, NullLogger.Instance));
        Assert.Contains("Invoice.CustomerId", error.Message);
        Assert.Contains("'Missing'", error.Message);
    }

    [Fact]
    public void AFilterOnAPropertyTheTargetDoesNotHave_LeavesOnlyThatRuleUnchecked()
    {
        // Same target, one rule with a broken filter: the other rule is still checked.
        var broken = Reference(filter: Where("Missing", PolicyOperator.EQUAL, "x"));
        var plain = Reference();
        var filtered = Reference(filter: Where("Status", PolicyOperator.EQUAL, "Active"));
        var invoice = Entity("Invoice", Field("A", broken), Field("B", plain), Field("C", filtered));

        var targets = ReferenceTargets.Build([invoice, Customer], strict: false, NullLogger.Instance);

        Assert.Null(targets.Find(broken));
        Assert.NotNull(targets.Find(plain));
        Assert.NotNull(targets.Find(filtered));
    }

    [Theory]
    [InlineData("ItemId")]
    [InlineData("Status")]
    public void AFilterOnTheTargetsIdOrAProperty_Resolves(string field)
    {
        var rule = Reference(filter: Where(field, PolicyOperator.EQUAL, "x"));
        var targets = ReferenceTargets.Build([Entity("Invoice", Field("CustomerId", rule)), Customer], strict: true, NullLogger.Instance);
        Assert.NotNull(targets.Find(rule));
    }

    [Fact]
    public void AFilterOnANestedProperty_Resolves_WhetherItIsStoredNestedOrAsADottedRow()
    {
        var nested = Entity("Customer", new FieldDefinitionResponse { Name = "Address", Type = "Address", Fields = [Field("City")] });
        var dotted = Entity("Customer", Field("Address.City"));
        var rule = Reference(filter: Where("Address.City", PolicyOperator.EQUAL, "Berlin"));

        foreach (var customer in new[] { nested, dotted })
            Assert.NotNull(ReferenceTargets.Build([Entity("Invoice", Field("CustomerId", rule)), customer], strict: true, NullLogger.Instance).Find(rule));
    }

    [Fact]
    public void AFilterThatDoesNotCompareAPropertyWithAFixedValue_IsLeftUnchecked()
    {
        var rule = Reference(filter: new PolicyRuleGroup
        {
            Rules = [new() { LeftOperand = "Status", Operator = PolicyOperator.EQUAL, RightSource = ConditionSource.AUTH, RightOperand = "UserId" }],
        });
        var targets = ReferenceTargets.Build([Entity("Invoice", Field("CustomerId", rule)), Customer], strict: false, NullLogger.Instance);
        Assert.Null(targets.Find(rule));
    }
}
