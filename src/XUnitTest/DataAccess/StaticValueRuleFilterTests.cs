using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;
using static XUnitTest.DataAccess.DataAccessTestSupport;

namespace XUnitTest.DataAccess;

/// <summary>
/// Row rules that compare a schema field with a value typed in the policy editor: the value arrives as text, so
/// numbers and dates are matched as numbers and dates too, and In / Not in take the list in either form.
/// </summary>
public class StaticValueRuleFilterTests
{
    private static PolicyRule Rule(string field, PolicyOperator op, object? value, params string[] operands) => new()
    {
        LeftSource = ConditionSource.SCHEMA_FIELD, LeftOperand = field, Operator = op,
        RightSource = ConditionSource.STATIC_VALUE, StaticValue = value, RightOperands = operands.ToList(),
    };

    private static BsonDocument FilterOf(PolicyRuleGroup group)
    {
        var policy = new DataAccessPolicy { PolicyType = PolicyType.RLS, Operation = PolicyOperation.READ, IsAllowPolicy = true, RuleGroup = group };
        var result = policy.EvaluatePolicy(User());
        Assert.True(result.IsAccessGranted);
        return result.DataFilter!;
    }

    private static BsonDocument FilterOf(PolicyRule rule) => FilterOf(new PolicyRuleGroup { Rules = [rule] });

    public static TheoryData<PolicyOperator, string, string> NumberComparisons => new()
    {
        { PolicyOperator.GREATER_THAN, "150", "$gt" },
        { PolicyOperator.GREATER_THAN_OR_EQUAL, "150.0", "$gte" },
        { PolicyOperator.LESS_THAN, " 150 ", "$lt" },
        { PolicyOperator.LESS_THAN_OR_EQUAL, "150", "$lte" },
    };

    [Theory]
    [MemberData(nameof(NumberComparisons))]
    public void ANumberTypedAsText_MatchesTheNumberOrTheText(PolicyOperator op, string typed, string mongo)
    {
        var filter = FilterOf(Rule("Price", op, typed));

        Assert.Equal(new BsonDocument("$or", new BsonArray
        {
            new BsonDocument("Price", new BsonDocument(mongo, 150.0)),
            new BsonDocument("Price", new BsonDocument(mongo, typed)),
        }), filter);
    }

    [Fact]
    public void Equal_AndNotEqual_MatchTheNumberTheSameWay()
    {
        Assert.Equal(new BsonDocument("$or", new BsonArray { new BsonDocument("Price", 150.0), new BsonDocument("Price", "150") }),
            FilterOf(Rule("Price", PolicyOperator.EQUAL, "150")));
        Assert.Equal(new BsonDocument("$and", new BsonArray
            {
                new BsonDocument("Price", new BsonDocument("$ne", 150.0)),
                new BsonDocument("Price", new BsonDocument("$ne", "150")),
            }),
            FilterOf(Rule("Price", PolicyOperator.NOT_EQUAL, "150")));
    }

    [Fact]
    public void AnIsoDate_MatchesTheDateOrTheText()
    {
        var filter = FilterOf(Rule("JoinedOn", PolicyOperator.GREATER_THAN_OR_EQUAL, "2025-01-01"));

        Assert.Equal(new BsonDocument("$or", new BsonArray
        {
            new BsonDocument("JoinedOn", new BsonDocument("$gte", new BsonDateTime(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)))),
            new BsonDocument("JoinedOn", new BsonDocument("$gte", "2025-01-01")),
        }), filter);
    }

    [Fact]
    public void PlainText_AndTokenValues_AreUnchanged()
    {
        Assert.Equal(new BsonDocument("Title", "A1"), FilterOf(Rule("Title", PolicyOperator.EQUAL, "A1")));
        Assert.Equal(new BsonDocument("OwnerId", "user-1"), FilterOf(Rule("OwnerId", PolicyOperator.EQUAL, "{{Auth.UserId}}")));
    }

    [Fact]
    public void ANumberComparisonInsideAnAndGroup_KeepsItsNumberForm()
    {
        var filter = FilterOf(new PolicyRuleGroup
        {
            LogicalOperator = PolicyLogicalOperator.AND,
            Rules = [Rule("Region", PolicyOperator.EQUAL, "US"), Rule("Price", PolicyOperator.GREATER_THAN_OR_EQUAL, "25")],
        });

        Assert.Contains("{ \"Price\" : { \"$gte\" : 25.0 } }", filter.ToJson());
        Assert.Contains("{ \"Region\" : \"US\" }", filter.ToJson());
    }

    [Theory]
    [InlineData(PolicyOperator.IN, "$in")]
    [InlineData(PolicyOperator.NOT_IN, "$nin")]
    public void InAndNotIn_OnACommaList_AreAPlainInOrNin(PolicyOperator op, string mongo)
    {
        Assert.Equal(new BsonDocument("Region", new BsonDocument(mongo, new BsonArray { "US", "ZZ" })),
            FilterOf(Rule("Region", op, "US,ZZ")));
        Assert.Equal(new BsonDocument("Title", new BsonDocument(mongo, new BsonArray { "A1", "B2", "Q" })),
            FilterOf(Rule("Title", op, "A1, B2 ,Q")));
    }

    [Fact]
    public void In_TakesTheListFromRightOperands_WhenTheStaticValueIsEmpty()
    {
        Assert.Equal(new BsonDocument("Region", new BsonDocument("$in", new BsonArray { "US", "ZZ" })),
            FilterOf(Rule("Region", PolicyOperator.IN, null, "US", "ZZ")));
    }

    [Fact]
    public void In_WithNumbers_MatchesEachAsNumberOrText()
    {
        Assert.Equal(new BsonDocument("Price", new BsonDocument("$in", new BsonArray { "10", 10.0, "20", 20.0 })),
            FilterOf(Rule("Price", PolicyOperator.IN, "10,20")));
    }

    [Fact]
    public void ContainOnAList_ComparesWithTheListNotTheText()
    {
        var json = FilterOf(Rule("Tags", PolicyOperator.CONTAIN, "a,b")).ToJson();

        Assert.Contains("[\"a\", \"b\"]", json);
        Assert.DoesNotContain("\"a,b\"", json);
    }
}
