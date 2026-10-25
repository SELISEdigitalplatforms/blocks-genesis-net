using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.DataAccess.Rules;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;

namespace XUnitTest.DataAccess.Rules;

/// <summary>
/// "Only when" conditions on a record (validation and access rules plan, section 1, phase 3): the condition becomes a
/// Mongo filter (BuildValidationConditionFilter) and BsonFilterMatcher checks it, so values compare as Mongo compares them.
/// </summary>
public class ValidationConditionTests
{
    private static PolicyRule Fixed(string left, PolicyOperator op, object? value) =>
        new() { LeftSource = ConditionSource.SCHEMA_FIELD, LeftOperand = left, Operator = op, RightSource = ConditionSource.STATIC_VALUE, StaticValue = value };

    private static PolicyRuleGroup All(params PolicyRule[] rules) => new() { Rules = [.. rules] };

    private static ConditionOutcome Evaluate(PolicyRuleGroup? when, BsonDocument record) => ValidationCondition.Evaluate(when, record);

    [Fact]
    public void NoCondition_AlwaysHolds()
    {
        Assert.Equal(ConditionOutcome.Holds, Evaluate(null, new BsonDocument()));
        Assert.Equal(ConditionOutcome.Holds, Evaluate(new PolicyRuleGroup(), new BsonDocument()));

        var noFilter = DataAccessPolicyHelper.BuildValidationConditionFilter(null);
        Assert.True(noFilter.IsAccessGranted);
        Assert.False(noFilter.RequiresDataFilter);
    }

    [Fact]
    public void AFixedValue_MatchesTheRecord()
    {
        var when = All(Fixed("Country", PolicyOperator.EQUAL, "DE"));
        Assert.Equal(ConditionOutcome.Holds, Evaluate(when, new BsonDocument("Country", "DE")));
        Assert.Equal(ConditionOutcome.DoesNotHold, Evaluate(when, new BsonDocument("Country", "AT")));
        Assert.Equal(ConditionOutcome.DoesNotHold, Evaluate(when, new BsonDocument()));
    }

    [Fact]
    public void DatesNumbersAndBools_CompareAsTheirType()
    {
        var after2000 = All(Fixed("BirthDate", PolicyOperator.GREATER_THAN, "2000-01-01"));
        Assert.Equal(ConditionOutcome.Holds, Evaluate(after2000, new BsonDocument("BirthDate", new DateTime(2001, 5, 1, 0, 0, 0, DateTimeKind.Utc))));
        Assert.Equal(ConditionOutcome.DoesNotHold, Evaluate(after2000, new BsonDocument("BirthDate", new DateTime(1999, 5, 1, 0, 0, 0, DateTimeKind.Utc))));

        Assert.Equal(ConditionOutcome.Holds, Evaluate(All(Fixed("Age", PolicyOperator.GREATER_THAN_OR_EQUAL, "18")), new BsonDocument("Age", 20)));
        Assert.Equal(ConditionOutcome.DoesNotHold, Evaluate(All(Fixed("Age", PolicyOperator.GREATER_THAN_OR_EQUAL, "18")), new BsonDocument("Age", 9)));

        // Matching "true" against a stored bool needs Genesis 4.4.5.
        Assert.Equal(ConditionOutcome.Holds, Evaluate(All(Fixed("Active", PolicyOperator.EQUAL, "true")), new BsonDocument("Active", true)));
        Assert.Equal(ConditionOutcome.DoesNotHold, Evaluate(All(Fixed("Active", PolicyOperator.EQUAL, "true")), new BsonDocument("Active", false)));
    }

    [Fact]
    public void IsOneOf_IsEmpty_AndAnyOfSeveral()
    {
        Assert.Equal(ConditionOutcome.Holds, Evaluate(All(Fixed("Status", PolicyOperator.IN, "Open,Closed")), new BsonDocument("Status", "Closed")));
        Assert.Equal(ConditionOutcome.Holds, Evaluate(All(Fixed("Reason", PolicyOperator.IS_NULL, null)), new BsonDocument()));

        var either = new PolicyRuleGroup
        {
            LogicalOperator = PolicyLogicalOperator.OR,
            Rules = [Fixed("Country", PolicyOperator.EQUAL, "DE")],
            NestedGroups = [All(Fixed("Country", PolicyOperator.EQUAL, "AT"), Fixed("Age", PolicyOperator.GREATER_THAN, "18"))],
        };
        Assert.Equal(ConditionOutcome.Holds, Evaluate(either, new BsonDocument { ["Country"] = "AT", ["Age"] = 30 }));
        Assert.Equal(ConditionOutcome.DoesNotHold, Evaluate(either, new BsonDocument { ["Country"] = "AT", ["Age"] = 10 }));
    }

    [Fact]
    public void Contains_MeansThePlainText()
    {
        // The policy builder puts "contains" into $regex unescaped; validation conditions escape it on a copy.
        var when = All(Fixed("Name", PolicyOperator.CONTAIN, "a.b"));
        Assert.Equal(ConditionOutcome.Holds, Evaluate(when, new BsonDocument("Name", "xA.By")));
        Assert.Equal(ConditionOutcome.DoesNotHold, Evaluate(when, new BsonDocument("Name", "axb")));
        Assert.Equal("a.b", when.Rules[0].StaticValue);   // the stored condition is not changed
    }

    [Fact]
    public void TwoProperties_CompareWithEachOther()
    {
        var less = All(new PolicyRule { LeftOperand = "Age", Operator = PolicyOperator.LESS_THAN, RightSource = ConditionSource.SCHEMA_FIELD, RightOperand = "Score" });
        Assert.Equal(ConditionOutcome.Holds, Evaluate(less, new BsonDocument { ["Age"] = 1, ["Score"] = 2 }));
        Assert.Equal(ConditionOutcome.DoesNotHold, Evaluate(less, new BsonDocument { ["Age"] = 3, ["Score"] = 2 }));
    }

    [Fact]
    public void AConditionThatCannotBeChecked_SaysSo_InsteadOfThrowing()
    {
        // Refused when saved, but a rule written straight to the database must not crash the write.
        Assert.Equal(ConditionOutcome.CannotCheck, Evaluate(All(Fixed("Name", PolicyOperator.REGEX, "a(b")), new BsonDocument("Name", "x")));
    }
}
