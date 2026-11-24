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
        Assert.DoesNotContain(result.Match.ToJson(), "null");
        Assert.DoesNotContain(result.Match.Values, v => v.IsBsonNull);
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
