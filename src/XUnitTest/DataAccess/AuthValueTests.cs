// Test names use underscores and tests await without ConfigureAwait, as everywhere in this project (xUnit1030).
#pragma warning disable CA1707, CA2007
using Blocks.Genesis;
using Blocks.Genesis.DataAccess;
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using static XUnitTest.DataAccess.DataAccessTestSupport;

namespace XUnitTest.DataAccess;

/// <summary>G1 (Permissions = token + role-bound) and G2 (Attributes.&lt;name&gt; from the database) in evaluation.</summary>
public class AuthValueTests
{
    private static readonly AccessPrincipal Enriched = User(roles: "manager") with
    {
        RolePermissions = ["orders::approve"],
        AccessAttributes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["region"] = ["EU", "US"],
            ["empty"] = [],
        }
    };

    [Fact]
    public void Permissions_AreTokenPlusRoleBound()
    {
        var principal = Enriched with { Permissions = ["p-token"] };

        var value = Assert.IsAssignableFrom<IReadOnlyList<string>>(DataAccessPolicyHelper.GetTokenValue(principal, "Permissions"));
        Assert.Equal(["p-token", "orders::approve"], value);

        var group = Group(PolicyLogicalOperator.AND, Rule(ConditionSource.AUTH, "permissions", PolicyOperator.CONTAIN, staticValue: "orders::approve"));
        Assert.True(Policy(PolicyType.RLS, PolicyOperation.READ, [], group).EvaluatePolicy(principal).IsAccessGranted);
        Assert.False(Policy(PolicyType.RLS, PolicyOperation.READ, [], group).EvaluatePolicy(User()).IsAccessGranted);
    }

    [Fact]
    public void Attributes_ResolveToStringList_UnknownIsNull()
    {
        Assert.Equal(["EU", "US"], (string[])DataAccessPolicyHelper.GetTokenValue(Enriched, "Attributes.Region")!);
        Assert.Empty((string[])DataAccessPolicyHelper.GetTokenValue(Enriched, "Attributes.empty")!);
        Assert.Null(DataAccessPolicyHelper.GetTokenValue(Enriched, "Attributes.unknown"));
        Assert.Null(DataAccessPolicyHelper.GetTokenValue(Enriched, "Attributes."));
        Assert.Null(DataAccessPolicyHelper.GetTokenValue(Enriched, "SomethingElse"));
        // Not enriched: never read from anywhere else.
        Assert.Null(DataAccessPolicyHelper.GetTokenValue(User(), "Attributes.region"));
    }

    [Fact]
    public void Attributes_AsRowFilter_FromRuleAndFromStaticExpression()
    {
        var byRule = Policy(PolicyType.RLS, PolicyOperation.READ, [],
            Group(PolicyLogicalOperator.AND, Rule(ConditionSource.SCHEMA_FIELD, "Region", PolicyOperator.IN, ConditionSource.AUTH, "Attributes.region")));
        var byExpression = Policy(PolicyType.RLS, PolicyOperation.READ, [],
            Group(PolicyLogicalOperator.AND, Rule(ConditionSource.SCHEMA_FIELD, "Region", PolicyOperator.IN, ConditionSource.STATIC_VALUE, staticValue: "{{Auth.Attributes.region}}")));

        foreach (var policy in new[] { byRule, byExpression })
        {
            var filter = policy.EvaluatePolicy(Enriched).DataFilter;
            Assert.True(BsonFilterMatcher.Matches(new BsonDocument("Region", "EU"), filter), filter.ToJson());
            Assert.False(BsonFilterMatcher.Matches(new BsonDocument("Region", "APAC"), filter), filter.ToJson());
        }
    }

    [Fact]
    public void Attributes_TokenRule_UnknownNameDenies()
    {
        var group = Group(PolicyLogicalOperator.AND, Rule(ConditionSource.AUTH, "Attributes.clearance", PolicyOperator.CONTAIN, staticValue: "secret"));

        Assert.False(Policy(PolicyType.RLS, PolicyOperation.READ, [], group).EvaluatePolicy(Enriched).IsAccessGranted);
    }

    [Fact]
    public void PolicyAuthUsage_FindsPermissionsAndAttributeNames()
    {
        var policies = new List<DataAccessPolicy>
        {
            RolePolicy(PolicyOperation.READ, "admin"),
            Policy(PolicyType.RLS, PolicyOperation.READ, [], new PolicyRuleGroup
            {
                Rules = [Rule(ConditionSource.AUTH, "Permissions", PolicyOperator.CONTAIN, staticValue: "x")],
                NestedGroups = [Group(PolicyLogicalOperator.OR,
                    Rule(ConditionSource.SCHEMA_FIELD, "Dept", PolicyOperator.IN, ConditionSource.AUTH, "Attributes.dept"),
                    Rule(ConditionSource.SCHEMA_FIELD, "Site", PolicyOperator.IN, staticValue: "{{Auth.Attributes.site}}"))]
            }),
        };

        var usage = PolicyAuthUsage.Of(policies);

        Assert.True(usage.UsesPermissions);
        Assert.Equal(["dept", "site"], usage.AttributeNames.Order(StringComparer.Ordinal));
        Assert.True(PolicyAuthUsage.Of([RolePolicy(PolicyOperation.READ, "admin")]).IsEmpty);
        Assert.True(PolicyAuthUsage.Of(null).IsEmpty);
    }
}

/// <summary>The database reads behind G1 and G2, against a real MongoDB.</summary>
[Trait("Category", "Integration")]
public sealed class AccessPrincipalEnricherTests : IDisposable
{
    private readonly MongoClient _client = new("mongodb://127.0.0.1:27017");
    private readonly IMongoDatabase _db;
    private readonly Mock<IDbContextProvider> _provider = new();
    private readonly ManualTime _time = new();

    private static readonly DataAccessPolicy UsesPermissions = Policy(PolicyType.RLS, PolicyOperation.READ, [],
        Group(PolicyLogicalOperator.AND, Rule(ConditionSource.AUTH, "permissions", PolicyOperator.CONTAIN, staticValue: "x")));

    private static readonly DataAccessPolicy UsesRegion = Policy(PolicyType.RLS, PolicyOperation.READ, [],
        Group(PolicyLogicalOperator.AND, Rule(ConditionSource.SCHEMA_FIELD, "Region", PolicyOperator.IN, ConditionSource.AUTH, "Attributes.region")));

    public AccessPrincipalEnricherTests()
    {
        _db = _client.GetDatabase("enricher_" + Guid.NewGuid().ToString("N"));
        _provider.Setup(p => p.GetDatabase("tenant-1")).Returns(_db);
    }

    public void Dispose() => _client.DropDatabase(_db.DatabaseNamespace.DatabaseName);

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private AccessPrincipalEnricher Enricher() => new(_provider.Object, new DataAccessOptions(), _time);

    private static BsonDocument Permission(string resource, string org, string[] roles, bool archived = false) => new()
    {
        { "_id", Guid.NewGuid().ToString("N") }, { "Resource", resource }, { "OrganizationId", org },
        { "Roles", new BsonArray(roles) }, { "IsArchived", archived }
    };

    private Task SeedPermissionsAsync(params BsonDocument[] permissions) =>
        _db.GetCollection<BsonDocument>("Permissions").InsertManyAsync(permissions);

    [Fact]
    public async Task RolePermissions_OwnOrgOnly_ArchivedAndOtherRolesExcluded()
    {
        await SeedPermissionsAsync(
            Permission("orders::approve", "org-1", ["manager"]),
            Permission("orders::approve", "org-1", ["admin", "manager"]),
            Permission("orders::delete", "org-1", ["admin"]),
            Permission("orders::archive", "org-1", ["manager"], archived: true),
            Permission("orders::other-org", "org-2", ["manager"]));

        var enriched = await Enricher().EnrichAsync(User(roles: "manager"), [UsesPermissions]);

        Assert.Equal(["orders::approve"], enriched.RolePermissions);
        Assert.Null(enriched.AccessAttributes);
    }

    [Fact]
    public async Task RolePermissions_BlankOrganization_IsDefault_IncludingRowsWithoutOrganization()
    {
        var legacy = Permission("legacy", "x", ["manager"]);
        legacy.Remove("OrganizationId");
        await SeedPermissionsAsync(Permission("tenant-wide", "default", ["manager"]), legacy, Permission("org-only", "org-1", ["manager"]));

        var enriched = await Enricher().EnrichAsync(User(roles: "manager") with { OrganizationId = "" }, [UsesPermissions]);

        Assert.Equal(["legacy", "tenant-wide"], enriched.RolePermissions.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RolePermissions_CachedPerRoleSet_UntilTheCacheTimeEnds()
    {
        await SeedPermissionsAsync(Permission("first", "org-1", ["manager"]));
        var enricher = Enricher();
        Assert.Equal(["first"], (await enricher.EnrichAsync(User(roles: "manager"), [UsesPermissions])).RolePermissions);

        await SeedPermissionsAsync(Permission("second", "org-1", ["manager"]));
        // The same role set (duplicates ignored): the same cache entry.
        Assert.Equal(["first"], (await enricher.EnrichAsync(User(roles: ["manager", "manager"]), [UsesPermissions])).RolePermissions);

        _time.Now += TimeSpan.FromSeconds(61);
        Assert.Equal(["first", "second"], (await enricher.EnrichAsync(User(roles: "manager"), [UsesPermissions])).RolePermissions.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AccessAttributes_FromUserDocument_ForTheCallersOrganization()
    {
        await _db.GetCollection<BsonDocument>("Users").InsertOneAsync(new BsonDocument
        {
            { "_id", "user-1" },
            { "Attributes", new BsonDocument("access", new BsonDocument
                {
                    { "org-1", new BsonDocument { { "region", new BsonArray { "EU", "US" } }, { "level", 3 } } },
                    { "org-2", new BsonDocument("region", new BsonArray { "APAC" }) },
                }) },
        });

        var enriched = await Enricher().EnrichAsync(User(), [UsesRegion]);

        Assert.Equal(["EU", "US"], enriched.AccessAttributes!["REGION"]);
        Assert.Equal(["3"], enriched.AccessAttributes["level"]);
        Assert.Empty(enriched.RolePermissions);
        Assert.Null(DataAccessPolicyHelper.GetTokenValue(enriched, "Attributes.missing"));
    }

    [Fact]
    public async Task AccessAttributes_UserWithoutAttributes_IsEmpty_NotNull()
    {
        await _db.GetCollection<BsonDocument>("Users").InsertOneAsync(new BsonDocument("_id", "user-1"));

        var enriched = await Enricher().EnrichAsync(User(), [UsesRegion]);

        Assert.NotNull(enriched.AccessAttributes);
        Assert.Empty(enriched.AccessAttributes!);
    }

    [Fact]
    public async Task NoDatabaseRead_WhenNoPolicyNeedsIt_RootTenant_OrNoContext()
    {
        var strict = new Mock<IDbContextProvider>(MockBehavior.Strict);
        var enricher = new AccessPrincipalEnricher(strict.Object);

        var user = User(roles: "manager");
        Assert.Same(user, await enricher.EnrichAsync(user, [RolePolicy(PolicyOperation.READ, "admin")]));
        var root = User(root: true);
        Assert.Same(root, await enricher.EnrichAsync(root, [UsesPermissions, UsesRegion]));
        Assert.Same(AccessPrincipal.Anonymous, await enricher.EnrichAsync(AccessPrincipal.Anonymous, [UsesPermissions]));
    }
}

/// <summary>G1 and G2 end to end: the resolver reads the database-backed Auth values the schema's policies use.</summary>
[Trait("Category", "Integration")]
public sealed class DataAccessResolverAuthValueTests : IDisposable
{
    private readonly MongoClient _client = new("mongodb://127.0.0.1:27017");
    private readonly IMongoDatabase _db;
    private readonly AccessPrincipal _principal = User(roles: "staff");

    public DataAccessResolverAuthValueTests() => _db = _client.GetDatabase("dataaccess_auth_" + Guid.NewGuid().ToString("N"));

    public void Dispose() => _client.DropDatabase(_db.DatabaseNamespace.DatabaseName);

    private static DataAccessPolicy Stored(DataAccessPolicy policy, string id)
    {
        policy.ItemId = id;
        policy.SchemaId = "sch-ticket";
        return policy;
    }

    private async Task SeedTicketAsync(params DataAccessPolicy[] policies)
    {
        await _db.GetCollection<SchemaDefinition>("SchemaDefinitions").InsertOneAsync(new SchemaDefinition
        {
            ItemId = "sch-ticket",
            SchemaName = "Ticket",
            ReadAccessLevel = SchemaAccessLevel.Custom,
            Fields = [new FieldDefinition { Name = "OrganizationId", Type = "String" }, new FieldDefinition { Name = "Region", Type = "String" }]
        });
        await _db.GetCollection<DataAccessPolicy>("DataAccessPolicys").InsertManyAsync(policies);
    }

    [Fact]
    public async Task ReadFilter_UsesRoleBoundPermissionsAndAccessAttributes_FromTheDatabase()
    {
        var byPermission = Stored(Policy(PolicyType.RLS, PolicyOperation.READ, [],
            Group(PolicyLogicalOperator.AND, Rule(ConditionSource.AUTH, "Permissions", PolicyOperator.CONTAIN, staticValue: "tickets::read-all"))), "pol-perm");
        var byRegion = Stored(Policy(PolicyType.RLS, PolicyOperation.READ, [],
            Group(PolicyLogicalOperator.AND, Rule(ConditionSource.SCHEMA_FIELD, "Region", PolicyOperator.IN, ConditionSource.AUTH, "Attributes.region"))), "pol-region");
        var sameOrg = Stored(Policy(PolicyType.RLS, PolicyOperation.READ, [],
            Group(PolicyLogicalOperator.AND, Rule(ConditionSource.SCHEMA_FIELD, "OrganizationId", PolicyOperator.EQUAL, ConditionSource.AUTH, "organizationId"))), "pol-read");
        await SeedTicketAsync(sameOrg, byPermission, byRegion);
        await _db.GetCollection<BsonDocument>("Users").InsertOneAsync(new BsonDocument
        {
            { "_id", "user-1" },
            { "Attributes", new BsonDocument("access", new BsonDocument("org-1", new BsonDocument("region", new BsonArray { "EU" }))) }
        });

        // Without the role-bound permission: same-org OR region rows.
        var filtered = await Resolver().ResolveReadFilterAsync("Ticket", new BsonDocument());
        Assert.True(filtered.IsAccessGranted);
        Assert.True(BsonFilterMatcher.Matches(new BsonDocument { { "OrganizationId", "org-9" }, { "Region", "EU" } }, filtered.Filter!));
        Assert.False(BsonFilterMatcher.Matches(new BsonDocument { { "OrganizationId", "org-9" }, { "Region", "US" } }, filtered.Filter!));

        // The staff role is bound to the permission in org-1: every row.
        await _db.GetCollection<BsonDocument>("Permissions").InsertOneAsync(new BsonDocument
        {
            { "_id", "perm-1" }, { "Resource", "tickets::read-all" }, { "OrganizationId", "org-1" },
            { "Roles", new BsonArray { "staff" } }, { "IsArchived", false }
        });
        var all = await Resolver().ResolveReadFilterAsync("Ticket", new BsonDocument());
        Assert.True(all.IsAccessGranted);
        Assert.Equal(0, all.Filter!.ElementCount);
    }

    private DataAccessResolver Resolver()
    {
        var provider = new Mock<IDbContextProvider>();
        provider.Setup(p => p.GetDatabase("tenant-1")).Returns(_db);
        var principals = new Mock<IAccessPrincipalAccessor>();
        principals.Setup(p => p.Current).Returns(() => _principal);
        return new DataAccessResolver(provider.Object, new DataAccessPolicyResolutionService(new SchemaPolicySource()), principals.Object);
    }
}
