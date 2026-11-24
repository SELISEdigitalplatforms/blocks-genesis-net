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
    // F1: a list field compared with the current user uses "includes" (EQUAL, element match) / "doesn't include"
    // (NOT_EQUAL): exact membership, never a pattern of the caller's claim.
    private static AccessPrincipal WithEmail(string email, params string[] roles) =>
        new("u1", email, "t1", "o1", roles, [], true, false) { HasContext = true };

    private static BsonDocument Emails(params string[] values) => new("Emails", new BsonArray(values));

    [Fact]
    public void Build_ListIncludesCurrentUser_ExactMemberOnly()
    {
        var group = DataAccessTestSupport.Group(PolicyLogicalOperator.AND, WhereAuth("Emails", "Email"));
        var result = DataAccessPolicyHelper.BuildAccessLookupFilter(group, WithEmail("bob@x.com"));

        Assert.Null(result.MissingAuthName);
        Assert.Equal(new BsonDocument("Emails", "bob@x.com"), result.Match);
        Assert.True(Matches(result.Match, Emails("ann@x.com", "bob@x.com")));
        Assert.False(Matches(result.Match, Emails("jimbob@x.com")));
        Assert.False(Matches(result.Match, Emails("bob@x.com.evil")));
        Assert.False(Matches(result.Match, Emails()));
    }

    [Fact]
    public void Build_ListDoesNotIncludeCurrentUser_ExactMemberOnly()
    {
        var group = DataAccessTestSupport.Group(PolicyLogicalOperator.AND, WhereAuth("Emails", "Email", PolicyOperator.NOT_EQUAL));
        var match = DataAccessPolicyHelper.BuildAccessLookupFilter(group, WithEmail("bob@x.com")).Match;

        Assert.Equal(BsonDocument.Parse("{ Emails: { $ne: 'bob@x.com' } }"), match);
        Assert.True(Matches(match, Emails("jimbob@x.com")));
        Assert.False(Matches(match, Emails("ann@x.com", "bob@x.com")));
    }

    [Theory]
    [InlineData("a.b+c@x.com", "aXb+c@x.com")]
    [InlineData("a.b+c@x.com", "a.bbc@x.com")]
    [InlineData("(x)*@x.com", "@x.com")]
    [InlineData("^.*$", "anything")]
    public void Build_ListIncludes_ClaimWithRegexMetacharacters_IsLiteral(string claim, string other)
    {
        var group = DataAccessTestSupport.Group(PolicyLogicalOperator.AND, WhereAuth("Emails", "Email"));
        var match = DataAccessPolicyHelper.BuildAccessLookupFilter(group, WithEmail(claim)).Match;

        Assert.DoesNotContain("$regex", match.ToJson());
        Assert.True(Matches(match, Emails(claim)));
        Assert.False(Matches(match, Emails(other)));
    }

    [Fact]
    public void Build_ListAgainstListClaim_AnyOfMembership_SameAsPolicyPath()
    {
        var includes = WhereAuth("Roles", "Roles");
        var excludes = WhereAuth("Roles", "Roles", PolicyOperator.NOT_EQUAL);
        var caller = WithEmail("bob@x.com", "admin", "hr");

        var inMatch = DataAccessPolicyHelper.BuildAccessLookupFilter(DataAccessTestSupport.Group(PolicyLogicalOperator.AND, includes), caller).Match;
        var ninMatch = DataAccessPolicyHelper.BuildAccessLookupFilter(DataAccessTestSupport.Group(PolicyLogicalOperator.AND, excludes), caller).Match;

        Assert.True(Matches(inMatch, new BsonDocument("Roles", new BsonArray { "hr", "user" })));
        Assert.False(Matches(inMatch, new BsonDocument("Roles", new BsonArray { "user", "superadmin" })));
        Assert.True(Matches(ninMatch, new BsonDocument("Roles", new BsonArray { "user", "superadmin" })));
        Assert.False(Matches(ninMatch, new BsonDocument("Roles", new BsonArray { "user", "admin" })));

        // The same filters an access policy builds for the same comparison.
        static BsonDocument PolicyFilter(PolicyRule rule, AccessPrincipal principal) =>
            new DataAccessPolicy { IsAllowPolicy = true, RuleGroup = DataAccessTestSupport.Group(PolicyLogicalOperator.AND, rule) }
                .EvaluatePolicy(principal).DataFilter!;
        Assert.Equal(PolicyFilter(includes, caller), inMatch);
        Assert.Equal(PolicyFilter(excludes, caller), ninMatch);
    }
}
