#pragma warning disable CA1707, CA2007
using Blocks.Genesis;
using Blocks.Genesis.DataAccess;
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;

namespace XUnitTest.DataAccess.Lookups;

/// <summary>EXISTS_IN / NOT_EXISTS_IN once resolved: the DB filter, the in-memory answer, and the backstop.</summary>
public sealed class LookupEvaluationTests
{
    private const string GenericMessage = "Access to {0} couldn't be checked. Ask the project's admin to review its access rules.";

    private static AccessPrincipal Principal => DataAccessTestSupport.User(roles: "user");

    private static PolicyRule Lookup(PolicyOperator op, LookupResolution? resolution, string field = "DepartmentId") => new()
    {
        LeftSource = ConditionSource.SCHEMA_FIELD,
        LeftOperand = field,
        Operator = op,
        Reference = new PolicyReference { SchemaName = "Department", FieldName = "ItemId" },
        Resolution = resolution,
    };

    private static DataAccessPolicy PolicyOf(params PolicyRule[] rules) => PolicyOf(PolicyLogicalOperator.AND, rules);

    private static DataAccessPolicy PolicyOf(PolicyLogicalOperator op, params PolicyRule[] rules) =>
        DataAccessTestSupport.Policy(PolicyType.RLS, PolicyOperation.READ, [], DataAccessTestSupport.Group(op, rules));

    private static PolicyRule AuthRole(string role) =>
        DataAccessTestSupport.Rule(ConditionSource.AUTH, "Roles", PolicyOperator.CONTAIN, staticValue: role);

    private static PolicyRule FieldEquals(string field, string value) =>
        DataAccessTestSupport.Rule(ConditionSource.SCHEMA_FIELD, field, PolicyOperator.EQUAL, staticValue: value);

    private static bool HasExpr(BsonValue value) => value switch
    {
        BsonDocument d => d.Names.Contains("$expr") || d.Values.Any(HasExpr),
        BsonArray a => a.Any(HasExpr),
        _ => false,
    };

    private static void AssertSameResult(PolicyEvaluationResult expected, PolicyEvaluationResult actual)
    {
        Assert.Equal(expected.IsAccessGranted, actual.IsAccessGranted);
        Assert.Equal(expected.RequiresDataFilter, actual.RequiresDataFilter);
        Assert.Equal(expected.DataFilter, actual.DataFilter);
    }

    [Fact]
    public void Filter_ExistsIn_Values_IsIn()
    {
        var values = new BsonValue[] { "X", 7, new BsonInt64(8), ObjectId.Parse("64b7f0c2a1b2c3d4e5f60718") };
        var result = PolicyOf(Lookup(PolicyOperator.EXISTS_IN, LookupResolution.Of(values))).EvaluatePolicy(Principal);

        Assert.True(result.IsAccessGranted);
        Assert.True(result.RequiresDataFilter);
        var expected = new BsonDocument("DepartmentId", new BsonDocument("$in", new BsonArray(values)));
        Assert.Equal(expected, result.DataFilter);
        // Stored types kept: no text alternatives, no conversion.
        var list = result.DataFilter["DepartmentId"]["$in"].AsBsonArray;
        Assert.Equal(new[] { BsonType.String, BsonType.Int32, BsonType.Int64, BsonType.ObjectId }, list.Select(v => v.BsonType));
    }

    [Fact]
    public void Filter_ExistsIn_Values_StringWithComma_IsNotSplit()
    {
        var result = PolicyOf(Lookup(PolicyOperator.EXISTS_IN, LookupResolution.Of(["a,b"]))).EvaluatePolicy(Principal);

        Assert.Equal(new BsonDocument("DepartmentId", new BsonDocument("$in", new BsonArray { "a,b" })), result.DataFilter);
    }

    [Fact]
    public void Filter_NotExistsIn_Values_IsNin()
    {
        var values = new BsonValue[] { "X", 7 };
        var result = PolicyOf(Lookup(PolicyOperator.NOT_EXISTS_IN, LookupResolution.Of(values))).EvaluatePolicy(Principal);

        Assert.True(result.IsAccessGranted);
        Assert.True(result.RequiresDataFilter);
        Assert.Equal(new BsonDocument("DepartmentId", new BsonDocument("$nin", new BsonArray(values))), result.DataFilter);
    }

    [Fact]
    public void Filter_Never_GrantsNothing_NoRowFilter()
    {
        var falseAuthOnly = PolicyOf(AuthRole("not-a-role")).EvaluatePolicy(Principal);

        foreach (var op in new[] { PolicyOperator.EXISTS_IN, PolicyOperator.NOT_EXISTS_IN })
        {
            var result = PolicyOf(Lookup(op, LookupResolution.Never)).EvaluatePolicy(Principal);
            Assert.False(result.IsAccessGranted);
            Assert.False(result.RequiresDataFilter);
            Assert.Equal(0, result.DataFilter.ElementCount);
            AssertSameResult(falseAuthOnly, result);

            // Combined exactly as a false Auth-only condition: AND refuses, OR keeps the other branch's filter.
            var and = PolicyOf(PolicyLogicalOperator.AND, Lookup(op, LookupResolution.Never), FieldEquals("Status", "Open")).EvaluatePolicy(Principal);
            var andRef = PolicyOf(PolicyLogicalOperator.AND, AuthRole("not-a-role"), FieldEquals("Status", "Open")).EvaluatePolicy(Principal);
            AssertSameResult(andRef, and);
            Assert.False(and.IsAccessGranted);

            var or = PolicyOf(PolicyLogicalOperator.OR, Lookup(op, LookupResolution.Never), FieldEquals("Status", "Open")).EvaluatePolicy(Principal);
            var orRef = PolicyOf(PolicyLogicalOperator.OR, AuthRole("not-a-role"), FieldEquals("Status", "Open")).EvaluatePolicy(Principal);
            AssertSameResult(orRef, or);
            Assert.False(HasExpr(or.DataFilter));
            Assert.False(HasExpr(and.DataFilter));
            Assert.False(HasExpr(result.DataFilter));
        }
    }

    [Fact]
    public void Filter_Always_Grants_NoRowFilter()
    {
        var trueAuthOnly = PolicyOf(AuthRole("user")).EvaluatePolicy(Principal);

        foreach (var op in new[] { PolicyOperator.EXISTS_IN, PolicyOperator.NOT_EXISTS_IN })
        {
            var result = PolicyOf(Lookup(op, LookupResolution.Always)).EvaluatePolicy(Principal);
            Assert.True(result.IsAccessGranted);
            Assert.False(result.RequiresDataFilter);
            Assert.Equal(0, result.DataFilter.ElementCount);
            AssertSameResult(trueAuthOnly, result);

            var and = PolicyOf(PolicyLogicalOperator.AND, Lookup(op, LookupResolution.Always), FieldEquals("Status", "Open")).EvaluatePolicy(Principal);
            var andRef = PolicyOf(PolicyLogicalOperator.AND, AuthRole("user"), FieldEquals("Status", "Open")).EvaluatePolicy(Principal);
            AssertSameResult(andRef, and);

            var or = PolicyOf(PolicyLogicalOperator.OR, Lookup(op, LookupResolution.Always), FieldEquals("Status", "Open")).EvaluatePolicy(Principal);
            var orRef = PolicyOf(PolicyLogicalOperator.OR, AuthRole("user"), FieldEquals("Status", "Open")).EvaluatePolicy(Principal);
            AssertSameResult(orRef, or);
            Assert.False(HasExpr(or.DataFilter));
            Assert.False(HasExpr(and.DataFilter));
        }
    }

    [Fact]
    public void Filter_FixedOutcomes_InEvaluatePolicies_LikeAuthOnly()
    {
        var never = new List<DataAccessPolicy> { PolicyOf(Lookup(PolicyOperator.EXISTS_IN, LookupResolution.Never)) }
            .EvaluatePolicies(Principal, PolicyOperation.READ, PolicyType.RLS);
        Assert.False(never.IsAccessGranted);

        var neverPlusOther = new List<DataAccessPolicy>
        {
            PolicyOf(Lookup(PolicyOperator.EXISTS_IN, LookupResolution.Never)),
            PolicyOf(FieldEquals("Status", "Open")),
        }.EvaluatePolicies(Principal, PolicyOperation.READ, PolicyType.RLS);
        Assert.True(neverPlusOther.IsAccessGranted);
        Assert.Equal(PolicyOf(FieldEquals("Status", "Open")).EvaluatePolicy(Principal).DataFilter, neverPlusOther.DataFilter);

        var always = new List<DataAccessPolicy> { PolicyOf(Lookup(PolicyOperator.NOT_EXISTS_IN, LookupResolution.Always)) }
            .EvaluatePolicies(Principal, PolicyOperation.READ, PolicyType.RLS);
        Assert.True(always.IsAccessGranted);
        Assert.False(always.RequiresDataFilter);
    }

    public static TheoryData<string, string, string, string, bool> MongoSemanticsCases => new()
    {
        // row, field, resolved values, operator, expected
        { "{ DepartmentId: 5 }", "DepartmentId", "[NumberLong(5)]", "EXISTS_IN", true },
        { "{ DepartmentId: 5 }", "DepartmentId", "[NumberLong(5)]", "NOT_EXISTS_IN", false },
        { "{ DepartmentId: NumberLong(5) }", "DepartmentId", "[5]", "EXISTS_IN", true },
        { "{ DepartmentId: 5.0 }", "DepartmentId", "[5]", "EXISTS_IN", true },
        { "{ DepartmentId: 5 }", "DepartmentId", "[6]", "EXISTS_IN", false },
        { "{ DepartmentId: 5 }", "DepartmentId", "[6]", "NOT_EXISTS_IN", true },
        { "{ DepartmentId: 'x' }", "DepartmentId", "['X']", "EXISTS_IN", false },
        { "{ DepartmentId: 'x' }", "DepartmentId", "['X']", "NOT_EXISTS_IN", true },
        { "{ DepartmentId: 'X' }", "DepartmentId", "['X']", "EXISTS_IN", true },
        { "{ DepartmentId: '5' }", "DepartmentId", "[5]", "EXISTS_IN", false },
        { "{ DepartmentId: '5' }", "DepartmentId", "[5]", "NOT_EXISTS_IN", true },
        { "{ DepartmentId: 5 }", "DepartmentId", "['5']", "EXISTS_IN", false },
        { "{ DepartmentId: 'a,b' }", "DepartmentId", "['a']", "EXISTS_IN", false },
        { "{ DepartmentId: ObjectId('64b7f0c2a1b2c3d4e5f60718') }", "DepartmentId", "[ObjectId('64b7f0c2a1b2c3d4e5f60718')]", "EXISTS_IN", true },
        // list field: any item
        { "{ DepartmentId: ['a', 'b'] }", "DepartmentId", "['b']", "EXISTS_IN", true },
        { "{ DepartmentId: ['a', 'b'] }", "DepartmentId", "['b']", "NOT_EXISTS_IN", false },
        { "{ DepartmentId: ['a', 'b'] }", "DepartmentId", "['c']", "EXISTS_IN", false },
        { "{ DepartmentId: ['a', 'b'] }", "DepartmentId", "['c']", "NOT_EXISTS_IN", true },
        { "{ DepartmentId: [1, NumberLong(2)] }", "DepartmentId", "[2.0]", "EXISTS_IN", true },
        { "{ DepartmentId: [] }", "DepartmentId", "['a']", "EXISTS_IN", false },
        { "{ DepartmentId: [] }", "DepartmentId", "['a']", "NOT_EXISTS_IN", true },
        // null / missing: not in the set
        { "{ DepartmentId: null }", "DepartmentId", "['a']", "EXISTS_IN", false },
        { "{ DepartmentId: null }", "DepartmentId", "['a']", "NOT_EXISTS_IN", true },
        { "{ Other: 'a' }", "DepartmentId", "['a']", "EXISTS_IN", false },
        { "{ Other: 'a' }", "DepartmentId", "['a']", "NOT_EXISTS_IN", true },
        // nested paths and lists of objects
        { "{ Department: { Id: 'a' } }", "Department.Id", "['a']", "EXISTS_IN", true },
        { "{ Department: { Id: 'a' } }", "Department.Id", "['a']", "NOT_EXISTS_IN", false },
        { "{ Department: { Name: 'a' } }", "Department.Id", "['a']", "NOT_EXISTS_IN", true },
        { "{ Members: [ { Id: 'a' }, { Id: 'b' } ] }", "Members.Id", "['b']", "EXISTS_IN", true },
        { "{ Members: [ { Id: 'a' }, { Id: 'b' } ] }", "Members.Id", "['b']", "NOT_EXISTS_IN", false },
        { "{ Members: [ { Id: 'a' }, { Name: 'b' } ] }", "Members.Id", "['c']", "NOT_EXISTS_IN", true },
        { "{ Members: [ { Id: 'a' }, { Name: 'b' } ] }", "Members.Id", "['c']", "EXISTS_IN", false },
        { "{ Members: [ { Tags: ['a'] }, { Tags: ['b', 'c'] } ] }", "Members.Tags", "['c']", "EXISTS_IN", true },
        { "{ Members: [ { Tags: ['a'] }, { Tags: ['b', 'c'] } ] }", "Members.Tags", "['c']", "NOT_EXISTS_IN", false },
        { "{ DepartmentId: [ { Id: 'a' }, 'b' ] }", "DepartmentId", "['b']", "EXISTS_IN", true },
        { "{ DepartmentId: { Id: 'a' } }", "DepartmentId", "['a']", "EXISTS_IN", false },
        { "{ DepartmentId: { Id: 'a' } }", "DepartmentId", "['a']", "NOT_EXISTS_IN", true },
        // empty resolved set
        { "{ DepartmentId: 'a' }", "DepartmentId", "[]", "EXISTS_IN", false },
        { "{ DepartmentId: 'a' }", "DepartmentId", "[]", "NOT_EXISTS_IN", true },
    };

    [Theory]
    [MemberData(nameof(MongoSemanticsCases))]
    public void Memory_MatchesMongoSemantics(string rowJson, string field, string valuesJson, string op, bool expected)
    {
        var row = BsonDocument.Parse(rowJson);
        var values = BsonDocument.Parse("{ v: " + valuesJson + " }")["v"].AsBsonArray.ToList();
        var rule = Lookup(Enum.Parse<PolicyOperator>(op), LookupResolution.Of(values), field);
        var policy = PolicyOf(rule);

        var filter = policy.EvaluatePolicy(Principal);
        Assert.True(filter.IsAccessGranted);
        Assert.False(HasExpr(filter.DataFilter));
        Assert.Equal(expected, BsonFilterMatcher.Matches(row, filter.DataFilter));

        Assert.Equal(expected, QueryPolicyRuleEvaluator.RowSatisfiesPolicy(Principal, policy, row.ToDictionary()));
        Assert.Equal(expected, QueryPolicyRuleEvaluator.EvaluateSingleRuleForRow(Principal, rule, row.ToDictionary()));
    }

    [Fact]
    public void Memory_RowWithBsonValues_MatchesByKey()
    {
        // Rows can also carry BsonValues (nested documents are kept as BsonDocument).
        var rule = Lookup(PolicyOperator.EXISTS_IN, LookupResolution.Of([new BsonInt64(5)]), "Department.Id");
        var row = new Dictionary<string, object> { ["Department"] = new BsonDocument("Id", 5) };
        Assert.True(QueryPolicyRuleEvaluator.EvaluateSingleRuleForRow(Principal, rule, row));

        var listRule = Lookup(PolicyOperator.NOT_EXISTS_IN, LookupResolution.Of(["b"]));
        var listRow = new Dictionary<string, object> { ["DepartmentId"] = new BsonArray { "a", "b" } };
        Assert.False(QueryPolicyRuleEvaluator.EvaluateSingleRuleForRow(Principal, listRule, listRow));
    }

    [Theory]
    [InlineData(PolicyOperator.EXISTS_IN)]
    [InlineData(PolicyOperator.NOT_EXISTS_IN)]
    public void Memory_FixedOutcomes(PolicyOperator op)
    {
        var row = BsonDocument.Parse("{ DepartmentId: 'a' }").ToDictionary();
        Assert.False(QueryPolicyRuleEvaluator.EvaluateSingleRuleForRow(Principal, Lookup(op, LookupResolution.Never), row));
        Assert.True(QueryPolicyRuleEvaluator.EvaluateSingleRuleForRow(Principal, Lookup(op, LookupResolution.Always), row));

        var empty = new Dictionary<string, object>();
        Assert.False(QueryPolicyRuleEvaluator.EvaluateSingleRuleForRow(Principal, Lookup(op, LookupResolution.Never), empty));
        Assert.True(QueryPolicyRuleEvaluator.EvaluateSingleRuleForRow(Principal, Lookup(op, LookupResolution.Always), empty));
    }

    [Theory]
    [InlineData(PolicyOperator.EXISTS_IN)]
    [InlineData(PolicyOperator.NOT_EXISTS_IN)]
    public void Unresolved_Throws_AccessCheckFailed(PolicyOperator op)
    {
        var rule = Lookup(op, resolution: null);
        var policy = PolicyOf(rule);
        var row = BsonDocument.Parse("{ DepartmentId: 'a' }").ToDictionary();

        var db = Assert.Throws<AccessCheckFailedException>(() => policy.EvaluatePolicy(Principal));
        var orGroup = Assert.Throws<AccessCheckFailedException>(() =>
            PolicyOf(PolicyLogicalOperator.OR, AuthRole("user"), rule).EvaluatePolicy(Principal));
        var memory = Assert.Throws<AccessCheckFailedException>(() =>
            QueryPolicyRuleEvaluator.RowSatisfiesPolicy(Principal, policy, row));

        // Called directly, the plain condition helpers have no resolution: unresolved there too.
        var condition = Assert.Throws<AccessCheckFailedException>(() => DataAccessPolicyHelper.EvaluateCondition("a", op, "a"));
        var conditionNull = Assert.Throws<AccessCheckFailedException>(() => DataAccessPolicyHelper.EvaluateCondition(null, op, null));
        var filter = Assert.Throws<AccessCheckFailedException>(() => DataAccessPolicyHelper.BuildConditionFilter(Principal, "DepartmentId", op, "a"));

        foreach (var ex in new[] { db, orGroup, memory, condition, conditionNull, filter })
        {
            Assert.Equal(string.Empty, ex.SchemaName);
            Assert.DoesNotContain("DepartmentId", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Department", ex.Message, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(ex.Reason));
        }
    }

    [Fact]
    public void UnknownOperator_Throws_AccessCheckFailed()
    {
        var unknown = (PolicyOperator)99;

        Assert.Throws<AccessCheckFailedException>(() => DataAccessPolicyHelper.BuildConditionFilter(Principal, "Status", unknown, "x"));
        Assert.Throws<AccessCheckFailedException>(() => DataAccessPolicyHelper.EvaluateCondition("x", unknown, "x"));
        Assert.Throws<AccessCheckFailedException>(() => DataAccessPolicyHelper.EvaluateCondition(null, unknown, null));
        Assert.Throws<AccessCheckFailedException>(() => DataAccessPolicyHelper.EvaluateCondition(new[] { "a" }, unknown, "a,b"));

        // Through policy evaluation: a field rule (DB filter), an Auth-only rule, and the in-memory row check.
        var fieldRule = DataAccessTestSupport.Rule(ConditionSource.SCHEMA_FIELD, "Status", unknown, staticValue: "x");
        Assert.Throws<AccessCheckFailedException>(() => PolicyOf(fieldRule).EvaluatePolicy(Principal));
        var authRule = DataAccessTestSupport.Rule(ConditionSource.AUTH, "Roles", unknown, staticValue: "user");
        Assert.Throws<AccessCheckFailedException>(() => PolicyOf(authRule).EvaluatePolicy(Principal));
        Assert.Throws<AccessCheckFailedException>(() =>
            QueryPolicyRuleEvaluator.RowSatisfiesPolicy(Principal, PolicyOf(fieldRule), new Dictionary<string, object> { ["Status"] = "x" }));
    }

    [Fact]
    public void Filter_ItemId_IsStoredId()
    {
        var exists = PolicyOf(Lookup(PolicyOperator.EXISTS_IN, LookupResolution.Of(["a"]), "ItemId")).EvaluatePolicy(Principal);
        Assert.Equal(new BsonDocument("_id", new BsonDocument("$in", new BsonArray { "a" })), exists.DataFilter);

        var notExists = PolicyOf(Lookup(PolicyOperator.NOT_EXISTS_IN, LookupResolution.Of(["a"]), "ItemId")).EvaluatePolicy(Principal);
        Assert.Equal(new BsonDocument("_id", new BsonDocument("$nin", new BsonArray { "a" })), notExists.DataFilter);
    }

    private static readonly BsonDocument[] StoredRows =
    [
        new() { { "_id", "a" }, { "Name", "A" } },
        new() { { "_id", "b" }, { "Name", "B" } },
        new() { { "_id", "c" }, { "Name", "C" } },
    ];

    [Fact]
    public void ItemId_NotExistsIn_DoesNotMatchEveryRow()
    {
        var policy = PolicyOf(Lookup(PolicyOperator.NOT_EXISTS_IN, LookupResolution.Of(["a", "c"]), "ItemId"));
        var filter = policy.EvaluatePolicy(Principal).DataFilter;

        // The stored document has _id only; the filter must not be on a missing ItemId field ($nin would then match all).
        Assert.False(filter.Contains("ItemId"));
        Assert.Equal(new[] { "b" }, StoredRows.Where(r => BsonFilterMatcher.Matches(r, filter)).Select(r => r["_id"].AsString));
        Assert.Equal(new[] { "b" }, StoredRows.Where(r => QueryPolicyRuleEvaluator.RowSatisfiesPolicy(Principal, policy, r.ToDictionary()))
            .Select(r => r["_id"].AsString));
    }

    [Fact]
    public void ItemId_ExistsIn_MatchesOnlyListedIds()
    {
        var policy = PolicyOf(Lookup(PolicyOperator.EXISTS_IN, LookupResolution.Of(["a", "c", "zz"]), "ItemId"));
        var filter = policy.EvaluatePolicy(Principal).DataFilter;

        Assert.Equal(new[] { "a", "c" }, StoredRows.Where(r => BsonFilterMatcher.Matches(r, filter)).Select(r => r["_id"].AsString));
        Assert.Equal(new[] { "a", "c" }, StoredRows.Where(r => QueryPolicyRuleEvaluator.RowSatisfiesPolicy(Principal, policy, r.ToDictionary()))
            .Select(r => r["_id"].AsString));
    }

    [Fact]
    public void ItemId_InMemory_FallsBackToItemIdKey_PrefersStoredId()
    {
        var exists = Lookup(PolicyOperator.EXISTS_IN, LookupResolution.Of(["a"]), "ItemId");
        var notExists = Lookup(PolicyOperator.NOT_EXISTS_IN, LookupResolution.Of(["a"]), "ItemId");

        var itemIdOnly = new Dictionary<string, object> { ["ItemId"] = "a" };
        Assert.True(QueryPolicyRuleEvaluator.EvaluateSingleRuleForRow(Principal, exists, itemIdOnly));
        Assert.False(QueryPolicyRuleEvaluator.EvaluateSingleRuleForRow(Principal, notExists, itemIdOnly));

        var both = new Dictionary<string, object> { ["_id"] = "b", ["ItemId"] = "a" };
        Assert.False(QueryPolicyRuleEvaluator.EvaluateSingleRuleForRow(Principal, exists, both));

        var neither = new Dictionary<string, object> { ["Name"] = "x" };
        Assert.False(QueryPolicyRuleEvaluator.EvaluateSingleRuleForRow(Principal, exists, neither));
        Assert.True(QueryPolicyRuleEvaluator.EvaluateSingleRuleForRow(Principal, notExists, neither));
    }

    [Fact]
    public void Exception_UnknownSchema_MessageHasNoSchema()
    {
        const string expected = "Access couldn't be checked. Ask the project's admin to review its access rules.";
        Assert.Equal(expected, new AccessCheckFailedException(string.Empty, "r").Message);
        Assert.Equal(expected, new AccessCheckFailedException(null!, "r").Message);

        var thrown = Assert.Throws<AccessCheckFailedException>(() =>
            PolicyOf(Lookup(PolicyOperator.EXISTS_IN, resolution: null)).EvaluatePolicy(Principal));
        Assert.Equal(expected, thrown.Message);
        Assert.Equal(string.Empty, thrown.SchemaName);
    }

    [Fact]
    public void Exception_MessageNamesSchemaOnly()
    {
        var ex = new AccessCheckFailedException("Employee", "EXISTS_IN on DepartmentId reached evaluation unresolved");

        Assert.Equal("Employee", ex.SchemaName);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.InvariantCulture, GenericMessage, "Employee"), ex.Message);
        Assert.Equal("EXISTS_IN on DepartmentId reached evaluation unresolved", ex.Reason);
        Assert.IsAssignableFrom<Exception>(ex);
    }
}
