// Test names use underscores and tests await without ConfigureAwait, as everywhere in this project (xUnit1030).
#pragma warning disable CA1707, CA2007
using Blocks.Genesis.DataAccess;
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;
using MongoDB.Driver;
using static XUnitTest.DataAccess.DataAccessTestSupport;

namespace XUnitTest.DataAccess;

/// <summary>
/// The in-memory matcher must agree with MongoDB on every filter the policy evaluator builds: each rule shape and
/// operator is turned into its filter, run against a real collection, and matched in memory on the same rows.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BsonFilterMatcherParityTests : IDisposable
{
    private readonly MongoClient _client = new("mongodb://127.0.0.1:27017");
    private readonly IMongoDatabase _db;
    private readonly IMongoCollection<BsonDocument> _rows;

    private static readonly AccessPrincipal Caller =
        new("user-1", "user@x.com", "tenant-1", "org-1", ["a", "x"], ["p1"], true, false) { HasContext = true };

    private static readonly BsonDocument[] Rows =
    [
        new() { { "_id", "1" }, { "Name", "Alice" }, { "Age", 30 }, { "Score", 150.0 }, { "Tags", new BsonArray { "a", "b" } },
                { "OrganizationId", "org-1" }, { "CreatedBy", "user-1" }, { "Manager", "user-1" }, { "Region", "US" },
                { "Born", new BsonDateTime(new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc)) } },
        new() { { "_id", "2" }, { "Name", "bob" }, { "Age", "30" }, { "Tags", new BsonArray { "c" } }, { "OrganizationId", "org-2" },
                { "CreatedBy", "user-2" }, { "Manager", "user-1" }, { "Region", BsonNull.Value } },
        new() { { "_id", "3" }, { "Name", "Carol" }, { "Age", 45 }, { "Score", 10 }, { "Tags", new BsonArray() }, { "OrganizationId", "org-1" },
                { "CreatedBy", "user-9" }, { "Region", "ZZ" }, { "Nested", new BsonDocument("Dept", "hr") },
                { "Born", new BsonDateTime(new DateTime(1980, 6, 1, 0, 0, 0, DateTimeKind.Utc)) } },
        new() { { "_id", "4" } },
        new() { { "_id", "5" }, { "Name", "alicia" }, { "Age", 30L }, { "Score", 99 }, { "Tags", "a" }, { "CreatedBy", "user-1" },
                { "Items", new BsonArray { new BsonDocument("Code", "x"), new BsonDocument("Code", "y") } } },
    ];

    public BsonFilterMatcherParityTests()
    {
        _db = _client.GetDatabase("matcher_" + Guid.NewGuid().ToString("N"));
        _rows = _db.GetCollection<BsonDocument>("Rows");
        _rows.InsertMany(Rows.Select(r => r.DeepClone().AsBsonDocument));
    }

    public void Dispose() => _client.DropDatabase(_db.DatabaseNamespace.DatabaseName);

    private static PolicyRule FieldStatic(string field, PolicyOperator op, object? value) =>
        Rule(ConditionSource.SCHEMA_FIELD, field, op, ConditionSource.STATIC_VALUE, staticValue: value);

    private static PolicyRule FieldAuth(string field, PolicyOperator op, string auth) =>
        Rule(ConditionSource.SCHEMA_FIELD, field, op, ConditionSource.AUTH, auth);

    private static PolicyRule AuthField(string auth, PolicyOperator op, params string[] fields)
    {
        var rule = Rule(ConditionSource.AUTH, auth, op, ConditionSource.SCHEMA_FIELD, fields[0]);
        if (fields.Length > 1)
            rule.RightOperands = [.. fields];
        return rule;
    }

    public static IEnumerable<object[]> Rules()
    {
        var rules = new Dictionary<string, PolicyRuleGroup>
        {
            ["eq text"] = Group(PolicyLogicalOperator.AND, FieldStatic("Name", PolicyOperator.EQUAL, "Alice")),
            ["ne text"] = Group(PolicyLogicalOperator.AND, FieldStatic("Name", PolicyOperator.NOT_EQUAL, "Alice")),
            ["eq typed number"] = Group(PolicyLogicalOperator.AND, FieldStatic("Age", PolicyOperator.EQUAL, "30")),
            ["ne typed number"] = Group(PolicyLogicalOperator.AND, FieldStatic("Age", PolicyOperator.NOT_EQUAL, "30")),
            ["gt number"] = Group(PolicyLogicalOperator.AND, FieldStatic("Age", PolicyOperator.GREATER_THAN, "29")),
            ["gte number"] = Group(PolicyLogicalOperator.AND, FieldStatic("Age", PolicyOperator.GREATER_THAN_OR_EQUAL, "30")),
            ["lt number"] = Group(PolicyLogicalOperator.AND, FieldStatic("Age", PolicyOperator.LESS_THAN, "45")),
            ["lte number"] = Group(PolicyLogicalOperator.AND, FieldStatic("Age", PolicyOperator.LESS_THAN_OR_EQUAL, "30")),
            ["gt date"] = Group(PolicyLogicalOperator.AND, FieldStatic("Born", PolicyOperator.GREATER_THAN, "1985-01-01")),
            ["contain"] = Group(PolicyLogicalOperator.AND, FieldStatic("Name", PolicyOperator.CONTAIN, "ali")),
            ["not contain"] = Group(PolicyLogicalOperator.AND, FieldStatic("Name", PolicyOperator.NOT_CONTAIN, "ali")),
            ["start with"] = Group(PolicyLogicalOperator.AND, FieldStatic("Name", PolicyOperator.START_WITH, "al")),
            ["end with"] = Group(PolicyLogicalOperator.AND, FieldStatic("Name", PolicyOperator.END_WITH, "ol")),
            ["regex"] = Group(PolicyLogicalOperator.AND, FieldStatic("Name", PolicyOperator.REGEX, "^[A-C]")),
            ["is null"] = Group(PolicyLogicalOperator.AND, FieldStatic("Region", PolicyOperator.IS_NULL, null)),
            ["is not null"] = Group(PolicyLogicalOperator.AND, FieldStatic("Region", PolicyOperator.IS_NOT_NULL, null)),
            ["in list"] = Group(PolicyLogicalOperator.AND, FieldStatic("Region", PolicyOperator.IN, "US,ZZ")),
            ["not in list"] = Group(PolicyLogicalOperator.AND, FieldStatic("Region", PolicyOperator.NOT_IN, "US,ZZ")),
            ["in list on array field"] = Group(PolicyLogicalOperator.AND, FieldStatic("Tags", PolicyOperator.IN, "b,c")),
            ["eq comma alternatives"] = Group(PolicyLogicalOperator.AND, FieldStatic("Name", PolicyOperator.EQUAL, "Alice,bob")),
            ["field eq auth"] = Group(PolicyLogicalOperator.AND, FieldAuth("CreatedBy", PolicyOperator.EQUAL, "userId")),
            ["field eq auth org"] = Group(PolicyLogicalOperator.AND, FieldAuth("OrganizationId", PolicyOperator.EQUAL, "organizationId")),
            ["field ne auth"] = Group(PolicyLogicalOperator.AND, FieldAuth("CreatedBy", PolicyOperator.NOT_EQUAL, "userId")),
            ["field contain auth roles"] = Group(PolicyLogicalOperator.AND, FieldAuth("Tags", PolicyOperator.CONTAIN, "roles")),
            ["field in auth roles"] = Group(PolicyLogicalOperator.AND, FieldAuth("Tags", PolicyOperator.IN, "roles")),
            ["field not in auth roles"] = Group(PolicyLogicalOperator.AND, FieldAuth("Tags", PolicyOperator.NOT_IN, "roles")),
            ["field not contain auth roles"] = Group(PolicyLogicalOperator.AND, FieldAuth("Tags", PolicyOperator.NOT_CONTAIN, "roles")),
            ["auth eq field"] = Group(PolicyLogicalOperator.AND, AuthField("userId", PolicyOperator.EQUAL, "CreatedBy")),
            ["auth eq any of fields"] = Group(PolicyLogicalOperator.AND, AuthField("userId", PolicyOperator.EQUAL, "CreatedBy", "Manager")),
            ["auth roles in field"] = Group(PolicyLogicalOperator.AND, AuthField("roles", PolicyOperator.IN, "Tags")),
            ["auth roles not in field"] = Group(PolicyLogicalOperator.AND, AuthField("roles", PolicyOperator.NOT_IN, "Tags")),
            ["auth gt field"] = Group(PolicyLogicalOperator.AND, AuthField("userId", PolicyOperator.GREATER_THAN, "CreatedBy")),
            ["field eq field"] = Group(PolicyLogicalOperator.AND, Rule(ConditionSource.SCHEMA_FIELD, "CreatedBy", PolicyOperator.EQUAL, ConditionSource.SCHEMA_FIELD, "Manager")),
            ["field gt field"] = Group(PolicyLogicalOperator.AND, Rule(ConditionSource.SCHEMA_FIELD, "Score", PolicyOperator.GREATER_THAN, ConditionSource.SCHEMA_FIELD, "Age")),
            ["field in field"] = Group(PolicyLogicalOperator.AND, Rule(ConditionSource.SCHEMA_FIELD, "Tags", PolicyOperator.IN, ConditionSource.SCHEMA_FIELD, "Tags")),
            ["static lt field"] = Group(PolicyLogicalOperator.AND, Rule(ConditionSource.STATIC_VALUE, "", PolicyOperator.LESS_THAN, ConditionSource.SCHEMA_FIELD, "Age", staticValue: "30")),
            ["nested field"] = Group(PolicyLogicalOperator.AND, FieldStatic("Nested.Dept", PolicyOperator.EQUAL, "hr")),
            ["list item field"] = Group(PolicyLogicalOperator.AND, FieldStatic("Items.Code", PolicyOperator.EQUAL, "y")),
            ["static auth expression"] = Group(PolicyLogicalOperator.AND, FieldStatic("CreatedBy", PolicyOperator.EQUAL, "{{Auth.UserId}}")),
            ["and group"] = Group(PolicyLogicalOperator.AND, FieldAuth("OrganizationId", PolicyOperator.EQUAL, "organizationId"), FieldStatic("Age", PolicyOperator.GREATER_THAN, "40")),
            ["or group"] = Group(PolicyLogicalOperator.OR, FieldAuth("CreatedBy", PolicyOperator.EQUAL, "userId"), FieldStatic("Region", PolicyOperator.EQUAL, "ZZ")),
        };
        return rules.Select(r => new object[] { r.Key, r.Value });
    }

    [Theory]
    [MemberData(nameof(Rules))]
    public async Task Matcher_AgreesWithMongo(string name, PolicyRuleGroup group)
    {
        var result = Policy(PolicyType.RLS, PolicyOperation.READ, [], group).EvaluatePolicy(Caller);
        Assert.True(result.RequiresDataFilter, name);

        var fromMongo = (await _rows.Find(result.DataFilter).Project(Builders<BsonDocument>.Projection.Include("_id")).ToListAsync())
            .Select(d => d["_id"].AsString).Order().ToList();
        var inMemory = Rows.Where(r => BsonFilterMatcher.Matches(r, result.DataFilter)).Select(r => r["_id"].AsString).Order().ToList();

        Assert.True(fromMongo.SequenceEqual(inMemory),
            $"{name}: filter {result.DataFilter.ToJson()} -> mongo [{string.Join(",", fromMongo)}] memory [{string.Join(",", inMemory)}]");
    }
}

public sealed class BsonFilterMatcherTests
{
    [Fact]
    public void UnknownOperator_Throws_SoCallersRefuse()
    {
        var row = new BsonDocument("Items", new BsonArray { new BsonDocument("A", 1) });

        Assert.Throws<NotSupportedException>(() => BsonFilterMatcher.Matches(row, new BsonDocument("Items", new BsonDocument("$elemMatch", new BsonDocument("A", 1)))));
        Assert.Throws<NotSupportedException>(() => BsonFilterMatcher.Matches(row, new BsonDocument("$where", "true")));
        Assert.Throws<NotSupportedException>(() => BsonFilterMatcher.Matches(row, new BsonDocument("$expr", new BsonDocument("$function", 1))));
    }

    [Fact]
    public void EmptyFilter_MatchesEveryRow_ItemIdFallsBackToId()
    {
        Assert.True(BsonFilterMatcher.Matches(new BsonDocument(), new BsonDocument()));
        Assert.True(BsonFilterMatcher.Matches(new BsonDocument("_id", "r1"), new BsonDocument("ItemId", "r1")));
        Assert.False(BsonFilterMatcher.Matches(new BsonDocument("_id", "r1"), new BsonDocument("ItemId", "r2")));
    }

    [Fact]
    public void Operators_ExistsSizeAllNor()
    {
        var row = new BsonDocument { { "Tags", new BsonArray { "a", "b" } }, { "Name", "x" } };

        Assert.True(BsonFilterMatcher.Matches(row, new BsonDocument("Name", new BsonDocument("$exists", true))));
        Assert.True(BsonFilterMatcher.Matches(row, new BsonDocument("Missing", new BsonDocument("$exists", false))));
        Assert.True(BsonFilterMatcher.Matches(row, new BsonDocument("Tags", new BsonDocument("$size", 2))));
        Assert.True(BsonFilterMatcher.Matches(row, new BsonDocument("Tags", new BsonDocument("$all", new BsonArray { "a", "b" }))));
        Assert.False(BsonFilterMatcher.Matches(row, new BsonDocument("$nor", new BsonArray { new BsonDocument("Name", "x") })));
    }

    [Fact]
    public void ReferencedPaths_ListsFieldKeysAndExpressionFields()
    {
        var filter = new BsonDocument("$and", new BsonArray
        {
            new BsonDocument("OrganizationId", "org-1"),
            new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { "$CreatedBy", "$Manager" })),
        });

        Assert.Equal(["CreatedBy", "Manager", "OrganizationId"], BsonFilterMatcher.ReferencedPaths(filter).Order(StringComparer.Ordinal));
    }
}
