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

/// <summary>The by-name write checks (G3, G4) and the database-backed Auth values (G1, G2) through the resolver.</summary>
[Trait("Category", "Integration")]
public sealed class DataAccessResolverWriteTests : IDisposable
{
    private readonly MongoClient _client = new("mongodb://127.0.0.1:27017");
    private readonly IMongoDatabase _db;
    private readonly DataAccessResolver _resolver;
    private AccessPrincipal _principal = User(roles: "staff");

    public DataAccessResolverWriteTests()
    {
        _db = _client.GetDatabase("dataaccess_write_" + Guid.NewGuid().ToString("N"));
        var provider = new Mock<IDbContextProvider>();
        provider.Setup(p => p.GetDatabase("tenant-1")).Returns(_db);
        var principals = new Mock<IAccessPrincipalAccessor>();
        principals.Setup(p => p.Current).Returns(() => _principal);
        _resolver = new DataAccessResolver(provider.Object, new DataAccessPolicyResolutionService(new SchemaPolicySource()), principals.Object);
    }

    public void Dispose() => _client.DropDatabase(_db.DatabaseNamespace.DatabaseName);

    private static DataAccessPolicy Stored(DataAccessPolicy policy, string id)
    {
        policy.ItemId = id;
        policy.SchemaId = "sch-ticket";
        return policy;
    }

    private static PolicyRuleGroup SameOrg() =>
        Group(PolicyLogicalOperator.AND, Rule(ConditionSource.SCHEMA_FIELD, "OrganizationId", PolicyOperator.EQUAL, ConditionSource.AUTH, "organizationId"));

    /// <summary>Ticket: write/edit only inside the caller's organization; Priority may be changed only by its creator.</summary>
    private async Task SeedTicketAsync(params DataAccessPolicy[] extra)
    {
        await _db.GetCollection<SchemaDefinition>("SchemaDefinitions").InsertOneAsync(new SchemaDefinition
        {
            ItemId = "sch-ticket",
            SchemaName = "Ticket",
            ReadAccessLevel = SchemaAccessLevel.Custom,
            WriteAccessLevel = SchemaAccessLevel.Custom,
            EditAccessLevel = SchemaAccessLevel.Custom,
            Fields =
            [
                new FieldDefinition { Name = "Title", Type = "String" },
                new FieldDefinition { Name = "OrganizationId", Type = "String" },
                new FieldDefinition { Name = "CreatedBy", Type = "String" },
                new FieldDefinition { Name = "Region", Type = "String" },
                new FieldDefinition { Name = "Priority", Type = "Int", EditAccessLevel = SchemaAccessLevel.Custom },
            ]
        });

        var priorityOwner = Policy(PolicyType.CLS, PolicyOperation.EDIT, ["Priority"],
            Group(PolicyLogicalOperator.AND, Rule(ConditionSource.SCHEMA_FIELD, "CreatedBy", PolicyOperator.EQUAL, ConditionSource.AUTH, "userId")));
        var policies = new List<DataAccessPolicy>
        {
            Stored(Policy(PolicyType.RLS, PolicyOperation.READ, [], SameOrg()), "pol-read"),
            Stored(Policy(PolicyType.RLS, PolicyOperation.WRITE, [], SameOrg()), "pol-write"),
            Stored(Policy(PolicyType.RLS, PolicyOperation.EDIT, [], SameOrg()), "pol-edit"),
            Stored(priorityOwner, "pol-priority"),
        };
        policies.AddRange(extra);
        await _db.GetCollection<DataAccessPolicy>("DataAccessPolicys").InsertManyAsync(policies);
    }

    private static BsonDocument Ticket(string org, string createdBy = "user-1", int priority = 1) => new()
    {
        { "_id", "t1" }, { "Title", "a" }, { "OrganizationId", org }, { "CreatedBy", createdBy }, { "Priority", priority }
    };

    [Fact]
    public async Task CheckInsertAsync_RowOutsideWriteFilter_RefusedWithForbidden()
    {
        await SeedTicketAsync();

        Assert.True((await _resolver.CheckInsertAsync("Ticket", Ticket("org-1"))).IsAccessGranted);

        var outside = await _resolver.CheckInsertAsync("Ticket", Ticket("org-2"));
        Assert.False(outside.IsAccessGranted);
        Assert.Equal(PolicyErrorCodes.Forbidden, outside.Evaluation.ErrorCode);
        Assert.Empty(outside.DeniedFields);
        var ex = Assert.Throws<BlocksForbiddenException>(outside.ThrowIfDenied);
        Assert.Equal("FORBIDDEN", ex.Code);
    }

    [Fact]
    public async Task ResolveInsertAsync_InputOutsideWriteFilter_Refused_EmptyInputIsTheGateAlone()
    {
        await SeedTicketAsync();

        Assert.True((await _resolver.ResolveInsertAsync("Ticket", new Dictionary<string, object?>())).IsAccessGranted);
        Assert.True((await _resolver.ResolveInsertAsync("Ticket", new Dictionary<string, object?> { ["Title"] = "a", ["OrganizationId"] = "org-1" })).IsAccessGranted);

        var outside = await _resolver.ResolveInsertAsync("Ticket", new Dictionary<string, object?> { ["Title"] = "a", ["OrganizationId"] = "org-2" });
        Assert.False(outside.IsAccessGranted);
        Assert.Null(outside.Input);
        Assert.Equal(PolicyErrorCodes.Forbidden, outside.Evaluation.ErrorCode);
    }

    [Fact]
    public async Task CheckUpdateAsync_MovingTheRowToAnotherOrganization_Refused()
    {
        await SeedTicketAsync();
        var stored = Ticket("org-1");

        Assert.True((await _resolver.CheckUpdateAsync("Ticket", stored, new BsonDocument("$set", new BsonDocument("Title", "b")))).IsAccessGranted);
        Assert.False((await _resolver.CheckUpdateAsync("Ticket", stored, new BsonDocument("$set", new BsonDocument("OrganizationId", "org-2")))).IsAccessGranted);
        Assert.False((await _resolver.CheckUpdateAsync("Ticket", Ticket("org-2"), new BsonDocument("$set", new BsonDocument("Title", "b")))).IsAccessGranted);
        // An operator not computed in memory on a field the row rule reads is refused; on another field it is not.
        Assert.False((await _resolver.CheckUpdateAsync("Ticket", stored, new BsonDocument("$rename", new BsonDocument("OrganizationId", "Org")))).IsAccessGranted);
        Assert.True((await _resolver.CheckUpdateAsync("Ticket", stored, new BsonDocument("$push", new BsonDocument("Title", "x")))).IsAccessGranted);
    }

    [Fact]
    public async Task CheckReplaceAsync_DataColumnRule_ReadsTheStoredRow_AndNamesTheField()
    {
        await SeedTicketAsync();

        // Own ticket: may change Priority.
        Assert.True((await _resolver.CheckReplaceAsync("Ticket", Ticket("org-1"), Ticket("org-1", priority: 5))).IsAccessGranted);

        // Someone else's ticket in the same org: may edit the title, not the priority.
        var others = Ticket("org-1", createdBy: "user-2");
        Assert.True((await _resolver.CheckReplaceAsync("Ticket", others, others.DeepClone().AsBsonDocument.Set("Title", "b"))).IsAccessGranted);
        var refused = await _resolver.CheckReplaceAsync("Ticket", others, Ticket("org-1", createdBy: "user-2", priority: 5));
        Assert.False(refused.IsAccessGranted);
        Assert.Equal(["Priority"], refused.DeniedFields);
        Assert.Contains("'Priority'", refused.Evaluation.ErrorMessage, StringComparison.Ordinal);

        // Claiming the row by also setting CreatedBy does not help: the stored row decides.
        Assert.False((await _resolver.CheckReplaceAsync("Ticket", others, Ticket("org-1", createdBy: "user-1", priority: 5))).IsAccessGranted);
    }

    [Fact]
    public async Task ResolveUpdateAsync_RefusedColumn_DeniesInsteadOfStripping()
    {
        await _db.GetCollection<SchemaDefinition>("SchemaDefinitions").InsertOneAsync(new SchemaDefinition
        {
            ItemId = "sch-ticket", SchemaName = "Ticket", EditAccessLevel = SchemaAccessLevel.Public,
            Fields = [new FieldDefinition { Name = "Title", Type = "String" }, new FieldDefinition { Name = "Owner", Type = "String", EditAccessLevel = SchemaAccessLevel.User }]
        });
        _principal = AccessPrincipal.Anonymous with { TenantId = "tenant-1" };

        var denied = await _resolver.ResolveUpdateAsync("Ticket", new BsonDocument("_id", "t1"), new Dictionary<string, object?> { ["Title"] = "b", ["Owner"] = "me" });

        Assert.False(denied.IsAccessGranted);
        Assert.Null(denied.Filter);
        Assert.Equal(["Owner"], denied.Evaluation.ExcludedFields);
    }

    [Fact]
    public async Task RootTenant_AndNonCustomSchema_AreUnchanged()
    {
        await SeedTicketAsync();
        _principal = User(root: true);
        Assert.True((await _resolver.CheckInsertAsync("Ticket", Ticket("org-2"))).IsAccessGranted);
        Assert.True((await _resolver.CheckUpdateAsync("Ticket", Ticket("org-2", "user-2"), new BsonDocument("$set", new BsonDocument("Priority", 9)))).IsAccessGranted);

        await _db.GetCollection<SchemaDefinition>("SchemaDefinitions").InsertOneAsync(new SchemaDefinition
        {
            ItemId = "sch-note", SchemaName = "Note",
            ReadAccessLevel = SchemaAccessLevel.Public, WriteAccessLevel = SchemaAccessLevel.Public, EditAccessLevel = SchemaAccessLevel.Public,
            Fields = [new FieldDefinition { Name = "Text", Type = "String" }]
        });
        _principal = User();
        Assert.True((await _resolver.CheckInsertAsync("Note", new BsonDocument("Text", "a"))).IsAccessGranted);
        Assert.True((await _resolver.CheckReplaceAsync("Note", new BsonDocument("Text", "a"), new BsonDocument("Text", "b"))).IsAccessGranted);
    }
}
