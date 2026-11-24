#pragma warning disable CA1707, CA2007
using Blocks.Genesis.DataAccess;
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;

namespace XUnitTest.DataAccess.Lookups;

public sealed class AccessLookupFilterTests
{
    private static AccessPrincipal Principal(string userId) =>
        new(userId, "u@x.com", "t1", "o1", [], [], true, false) { HasContext = true };

    private static PolicyRule WhereAuth(string field, string auth, PolicyOperator op = PolicyOperator.EQUAL) =>
        DataAccessTestSupport.Rule(ConditionSource.SCHEMA_FIELD, field, op, ConditionSource.AUTH, auth);

    private static PolicyRule WhereStatic(string field, object? value, PolicyOperator op = PolicyOperator.EQUAL) =>
        DataAccessTestSupport.Rule(ConditionSource.SCHEMA_FIELD, field, op, ConditionSource.STATIC_VALUE, staticValue: value);

    private static bool ContainsNull(BsonValue value) => value switch
    {
        BsonNull => true,
        BsonDocument d => d.Values.Any(ContainsNull),
        BsonArray a => a.Any(ContainsNull),
        _ => false,
    };

    private static bool Matches(BsonDocument filter, BsonDocument row) =>
        MatcherFor(filter, row);

    private static bool MatcherFor(BsonDocument filter, BsonDocument row) =>
        Blocks.Genesis.DataAccess.Evaluation.BsonFilterMatcher.Matches(row, filter);

    [Fact]
    public void Build_NumericStatic_MatchesStoredInt()
    {
        var group = DataAccessTestSupport.Group(PolicyLogicalOperator.AND, WhereStatic("Age", "150", PolicyOperator.GREATER_THAN));
        var match = DataAccessPolicyHelper.BuildAccessLookupFilter(group, Principal("u1")).Match;
        Assert.True(Matches(match, new BsonDocument("Age", 200)));
        Assert.False(Matches(match, new BsonDocument("Age", 100)));
    }

    [Fact]
    public void Build_BoolStatic_MatchesStoredBool()
    {
        var group = DataAccessTestSupport.Group(PolicyLogicalOperator.AND, WhereStatic("Active", "true"));
        var match = DataAccessPolicyHelper.BuildAccessLookupFilter(group, Principal("u1")).Match;
        Assert.True(Matches(match, new BsonDocument("Active", true)));
        Assert.False(Matches(match, new BsonDocument("Active", false)));
    }

    [Fact]
    public void Build_ContainsOnArrayField_UsesCollectionPath()
    {
        var group = DataAccessTestSupport.Group(PolicyLogicalOperator.AND, WhereStatic("Tags", "red,blue", PolicyOperator.CONTAIN));
        var match = DataAccessPolicyHelper.BuildAccessLookupFilter(group, Principal("u1")).Match;
        Assert.True(match.Contains("$expr"));
        Assert.True(Matches(match, new BsonDocument("Tags", new BsonArray { "red", "blue", "green" })));
        Assert.False(Matches(match, new BsonDocument("Tags", new BsonArray { "red", "green" })));
    }

    [Fact]
    public void Build_EmployeeCase()
    {
        var group = DataAccessTestSupport.Group(PolicyLogicalOperator.AND, WhereAuth("UserId", "UserId"));

        var result = DataAccessPolicyHelper.BuildAccessLookupFilter(group, Principal("u1"));

        Assert.Null(result.MissingAuthName);
        Assert.Equal(new BsonDocument("UserId", "u1"), result.Match);
    }

    [Fact]
    public void Build_ItemIdMapsToId_Nested()
    {
        var nested = DataAccessTestSupport.Group(PolicyLogicalOperator.OR,
            WhereStatic("ItemId", "a"), WhereStatic("Status", "Active"));
        var group = new PolicyRuleGroup
        {
            Rules = [WhereStatic("ItemId", "b", PolicyOperator.NOT_EQUAL)],
            NestedGroups = [nested],
        };

        var result = DataAccessPolicyHelper.BuildAccessLookupFilter(group, Principal("u1"));

        Assert.DoesNotContain("ItemId", result.Match.ToJson());
        Assert.Equal(
            BsonDocument.Parse("{ $and: [ { _id: { $ne: 'b' } }, { $or: [ { _id: 'a' }, { Status: 'Active' } ] } ] }"),
            result.Match);
    }

    [Fact]
    public void Build_MissingAuth_ReportsName_NoNullFilter()
    {
        var group = DataAccessTestSupport.Group(PolicyLogicalOperator.AND,
            WhereAuth("UserId", "UserId"), WhereStatic("Owner", "{{auth.Email}}"));

        var result = DataAccessPolicyHelper.BuildAccessLookupFilter(group, AccessPrincipal.Anonymous);

        Assert.Equal("UserId", result.MissingAuthName);
        Assert.False(ContainsNull(result.Match));
        Assert.Empty(result.Match);
    }

    [Fact]
    public void Build_MissingStaticAuthExpression_ReportsName()
    {
        var group = DataAccessTestSupport.Group(PolicyLogicalOperator.AND, WhereStatic("Owner", "{{auth.Email}}"));
        var result = DataAccessPolicyHelper.BuildAccessLookupFilter(group, AccessPrincipal.Anonymous);
        Assert.Equal("Email", result.MissingAuthName);
        Assert.Empty(result.Match);
    }

    [Fact]
    public void Build_NullFilter_IsEmptyMatch()
    {
        var result = DataAccessPolicyHelper.BuildAccessLookupFilter(null, Principal("u1"));
        Assert.Null(result.MissingAuthName);
        Assert.Empty(result.Match);
    }

    [Fact]
    public void Build_ExistsInInsideFilter_Throws()
    {
        var group = DataAccessTestSupport.Group(PolicyLogicalOperator.AND, new PolicyRule
        {
            LeftOperand = "A", Operator = PolicyOperator.EXISTS_IN, Reference = new PolicyReference { SchemaName = "X" },
        });
        Assert.Throws<InvalidOperationException>(() => DataAccessPolicyHelper.BuildAccessLookupFilter(group, Principal("u1")));
    }

    [Fact]
    public void Build_AuthOnLeft_Throws()
    {
        var group = DataAccessTestSupport.Group(PolicyLogicalOperator.AND,
            DataAccessTestSupport.Rule(ConditionSource.AUTH, "UserId", PolicyOperator.EQUAL, ConditionSource.STATIC_VALUE, staticValue: "x"));
        Assert.Throws<InvalidOperationException>(() => DataAccessPolicyHelper.BuildAccessLookupFilter(group, Principal("u1")));
    }
}
