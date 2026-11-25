using Blocks.Genesis.DataAccess.Lookups;
#pragma warning disable CA1707, CA2007
using System.Text.Json;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

namespace XUnitTest.DataAccess.Lookups;

public sealed class PolicyReferenceSerializationTests
{
    private static PolicyRule Existing() => new()
    {
        LeftOperand = "Email",
        Operator = PolicyOperator.EQUAL,
        RightSource = ConditionSource.STATIC_VALUE,
        StaticValue = "a@b.c",
    };

    [Fact]
    public void ExistingRule_RoundTrips_ByteIdentical()
    {
        var before = Existing().ToBson();
        var back = BsonSerializer.Deserialize<PolicyRule>(before);
        var after = back.ToBson();

        Assert.Equal(before, after);
        Assert.False(Existing().ToBsonDocument().Contains("Reference"));
    }

    [Fact]
    public void ExistsInRule_RoundTrips()
    {
        var rule = Existing();
        rule.Operator = PolicyOperator.EXISTS_IN;
        rule.Reference = new PolicyReference
        {
            SchemaName = "Orders",
            FieldName = "CustomerId",
            Filter = new PolicyRuleGroup { Rules = [new PolicyRule { LeftOperand = "Status", StaticValue = "Open" }] },
        };

        var doc = rule.ToBsonDocument();
        Assert.Equal("EXISTS_IN", doc["Operator"].AsString);

        var back = BsonSerializer.Deserialize<PolicyRule>(doc);
        Assert.Equal(PolicyOperator.EXISTS_IN, back.Operator);
        Assert.NotNull(back.Reference);
        Assert.Equal("Orders", back.Reference!.SchemaName);
        Assert.Equal("CustomerId", back.Reference.FieldName);
        Assert.Equal("Status", back.Reference.Filter!.Rules[0].LeftOperand);
        Assert.Equal("ItemId", new PolicyReference().FieldName);
    }

    [Fact]
    public void Resolution_IsNeverSerialized()
    {
        var rule = Existing();
        rule.Resolution = LookupResolution.Of([new BsonString("x")]);

        Assert.DoesNotContain("Resolution", rule.ToBsonDocument().Names);
        Assert.DoesNotContain("resolution", JsonSerializer.Serialize(rule), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Json_OmitsNullReference()
    {
        Assert.DoesNotContain("reference", JsonSerializer.Serialize(Existing()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OperatorNumbers_AreAppended()
    {
        Assert.Equal(14, (int)PolicyOperator.REGEX);
        Assert.Equal(15, (int)PolicyOperator.EXISTS_IN);
        Assert.Equal(16, (int)PolicyOperator.NOT_EXISTS_IN);
    }
}
