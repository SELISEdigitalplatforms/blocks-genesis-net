#pragma warning disable CA1707, CA2007
using System.Collections.Concurrent;
using Blocks.Genesis;
using Blocks.Genesis.DataAccess;
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.DataAccess.Lookups;
using Blocks.Genesis.Entities.Data;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Moq;
using static XUnitTest.DataAccess.DataAccessTestSupport;
using static XUnitTest.DataAccess.Lookups.AccessLookupFixtures;

namespace XUnitTest.DataAccess;

/// <summary>
/// Cross-schema rules (EXISTS_IN / NOT_EXISTS_IN) through the by-name resolver the platform services use, against a
/// real MongoDB (localhost:27017, like <see cref="DataAccessResolverTests"/>). Every command sent to the test database
/// is recorded, so a schema without such rules can be shown to cost no extra query.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DataAccessResolverLookupTests : IDisposable
{
    private const string MongoConnectionString = "mongodb://127.0.0.1:27017";
    private readonly string _dbName = "dataaccess_lookup_" + Guid.NewGuid().ToString("N");
    private readonly ConcurrentQueue<(string Command, string Collection)> _commands = new();
    private readonly MongoClient _client;
    private readonly IMongoDatabase _db;
    private readonly Mock<IDbContextProvider> _contextProvider = new();
    private readonly Mock<IAccessPrincipalAccessor> _principals = new();

    public DataAccessResolverLookupTests()
    {
        var settings = MongoClientSettings.FromConnectionString(MongoConnectionString);
        settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(e =>
        {
            if (e.DatabaseNamespace?.DatabaseName == _dbName)
                _commands.Enqueue((e.CommandName, e.Command.TryGetValue(e.CommandName, out var target) ? target.ToString()! : string.Empty));
        });
        _client = new MongoClient(settings);
        _db = _client.GetDatabase(_dbName);
        _contextProvider.Setup(p => p.GetDatabase("tenant-1")).Returns(_db);
        _principals.Setup(p => p.Current).Returns(() => DataAccessTestSupport.User());
    }

    public void Dispose() => _client.DropDatabase(_dbName);

    private DataAccessResolver Resolver(bool systemEntitiesOnly = true) =>
        new(_contextProvider.Object, new DataAccessPolicyResolutionService(new SchemaPolicySource()), _principals.Object,
            new DataAccessOptions { SystemEntitiesOnly = systemEntitiesOnly });

    /// <summary>A system schema whose rows name a role; its READ policies are given.</summary>
    private async Task SeedRoleGrantAsync(params DataAccessPolicy[] policies)
    {
        await _db.GetCollection<SchemaDefinition>("SchemaDefinitions").InsertOneAsync(new SchemaDefinition
        {
            ItemId = "sch-rolegrant",
            SchemaName = "RoleGrant",
            SchemaType = SchemaType.Entity,
            IsSystemEntity = true,
            CollectionName = "RoleGrants",
            ReadAccessLevel = SchemaAccessLevel.Custom,
            Fields = [new FieldDefinition { Name = "RoleName", Type = "String" }, new FieldDefinition { Name = "Resource", Type = "String" }]
        });
        foreach (var policy in policies)
            policy.SchemaId = "sch-rolegrant";
        if (policies.Length > 0)
            await _db.GetCollection<DataAccessPolicy>("DataAccessPolicys").InsertManyAsync(policies);
        await _db.GetCollection<BsonDocument>("RoleGrants").InsertManyAsync(
        [
            new BsonDocument { ["_id"] = "g-admin", ["RoleName"] = "admin" },
            new BsonDocument { ["_id"] = "g-hidden", ["RoleName"] = "hidden" },
            new BsonDocument { ["_id"] = "g-gone", ["RoleName"] = "gone" },
            new BsonDocument { ["_id"] = "g-none", ["Resource"] = "x" },
        ]);
    }

    /// <summary>The system Role schema (a verified lookup target) and its rows.</summary>
    private async Task SeedRoleAsync()
    {
        await _db.GetCollection<SchemaDefinition>("SchemaDefinitions").InsertOneAsync(new SchemaDefinition
        {
            ItemId = "sch-role",
            SchemaName = "Role",
            SchemaType = SchemaType.Entity,
            IsSystemEntity = true,
            CollectionName = "Roles",
            ReadAccessLevel = SchemaAccessLevel.Custom,
            Fields = [new FieldDefinition { Name = "Name", Type = "String" }, new FieldDefinition { Name = "Description", Type = "String" }]
        });
        await _db.GetCollection<BsonDocument>("Roles").InsertManyAsync(
        [
            new BsonDocument { ["_id"] = "r1", ["Name"] = "admin", ["Description"] = "visible" },
            new BsonDocument { ["_id"] = "r2", ["Name"] = "hidden", ["Description"] = "secret" },
            new BsonDocument { ["_id"] = "r3", ["Name"] = "gone", ["Description"] = "visible", ["IsDeleted"] = true },
        ]);
    }

    /// <summary>A tenant schema (not a system entity) named Department, with a row whose Name is "admin".</summary>
    private async Task SeedTenantDepartmentAsync()
    {
        await _db.GetCollection<SchemaDefinition>("SchemaDefinitions").InsertOneAsync(new SchemaDefinition
        {
            ItemId = "sch-department",
            SchemaName = "Department",
            SchemaType = SchemaType.Entity,
            IsSystemEntity = false,
            CollectionName = "Departments",
            Fields = [new FieldDefinition { Name = "Name", Type = "String" }]
        });
        await _db.GetCollection<BsonDocument>("Departments").InsertOneAsync(new BsonDocument { ["_id"] = "d1", ["Name"] = "admin" });
    }

    private async Task<List<string>> RowIdsAsync(string collection, BsonDocument filter) =>
        (await _db.GetCollection<BsonDocument>(collection).Find(filter).ToListAsync()).Select(r => r["_id"].AsString).Order().ToList();

    private List<(string Command, string Collection)> TakeCommands()
    {
        var taken = new List<(string, string)>();
        while (_commands.TryDequeue(out var command))
            taken.Add(command);
        return taken;
    }

    [Fact]
    public async Task SystemSchemaPolicy_ExistsInRole_FiltersRows()
    {
        await SeedRoleAsync();
        await SeedRoleGrantAsync(Rls(PolicyOperation.READ, Lookup("RoleName", "Role", "Name", where: WhereStatic("Description", "visible"))));
        TakeCommands();

        var read = await Resolver().ResolveReadAsync("RoleGrant", new BsonDocument(), null);

        Assert.True(read.IsAccessGranted);
        Assert.Single(TakeCommands(), c => c is ("aggregate", "Roles"));
        // "hidden" is outside the WHERE filter, "gone" is a deleted role, g-none has no role.
        Assert.Equal(["g-admin"], await RowIdsAsync("RoleGrants", read.Filter!));
    }

    [Fact]
    public async Task SystemSchemaPolicy_NotExistsInRole_FiltersRows()
    {
        await SeedRoleAsync();
        await SeedRoleGrantAsync(Rls(PolicyOperation.READ, Lookup("RoleName", "Role", "Name", PolicyOperator.NOT_EXISTS_IN, WhereStatic("Description", "visible"))));

        var read = await Resolver().ResolveReadAsync("RoleGrant", new BsonDocument(), null);

        Assert.True(read.IsAccessGranted);
        Assert.Equal(["g-gone", "g-hidden", "g-none"], await RowIdsAsync("RoleGrants", read.Filter!));
    }

    [Fact]
    public async Task SchemaWithoutLookups_NoExtraQueries()
    {
        await SeedRoleAsync();
        await SeedRoleGrantAsync(RoleRead("admin"), OwnerPolicy(PolicyOperation.READ));
        TakeCommands();

        var read = await Resolver().ResolveReadAsync("RoleGrant", new BsonDocument(), null);

        Assert.True(read.IsAccessGranted);
        // Exactly what loading the schema always cost: its definition, policies and validations.
        Assert.Equal(
            [("find", "SchemaDefinitions"), ("find", "DataAccessPolicys"), ("find", "DataValidations")],
            TakeCommands());
    }

    [Fact]
    public async Task SchemaWithLookupsOnlyForOtherOperations_NoExtraQueries()
    {
        await SeedRoleAsync();
        await SeedRoleGrantAsync(RoleRead("admin"), Rls(PolicyOperation.EDIT, Lookup("RoleName", "Role", "Name")));
        TakeCommands();

        await Resolver().ResolveReadAsync("RoleGrant", new BsonDocument(), null);

        Assert.Equal(
            [("find", "SchemaDefinitions"), ("find", "DataAccessPolicys"), ("find", "DataValidations")],
            TakeCommands());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SystemSchemaPolicy_TenantTarget_GrantsNothingThroughRule(bool systemEntitiesOnly)
    {
        await SeedTenantDepartmentAsync();
        await SeedRoleGrantAsync(Rls(PolicyOperation.READ, Lookup("RoleName", "Department", "Name")));
        TakeCommands();

        var read = await Resolver(systemEntitiesOnly).ResolveReadAsync("RoleGrant", new BsonDocument(), null);

        Assert.False(read.IsAccessGranted);
        Assert.Null(read.Filter);
        Assert.DoesNotContain(TakeCommands(), c => c.Collection == "Departments");
    }

    [Fact]
    public async Task UnresolvedLookup_AccessCheckFailed_NamesTheSchema()
    {
        // A lookup in a deny policy is never resolved, so evaluation refuses the request.
        await SeedRoleAsync();
        var deny = Rls(PolicyOperation.READ, Lookup("RoleName", "Role", "Name"));
        deny.IsAllowPolicy = false;
        await SeedRoleGrantAsync(RoleRead("admin"), deny);

        var ex = await Assert.ThrowsAsync<AccessCheckFailedException>(() => Resolver().ResolveReadAsync("RoleGrant", new BsonDocument(), null));

        Assert.Equal("RoleGrant", ex.SchemaName);
        Assert.Contains("RoleGrant", ex.Message);
        Assert.NotEmpty(ex.Reason);
        Assert.IsType<AccessCheckFailedException>(ex.InnerException);
    }

    [Fact]
    public async Task UpdateUsesEditAndReadLookups()
    {
        await SeedRoleAsync();
        await SeedRoleGrantAsync(Rls(PolicyOperation.READ, Lookup("RoleName", "Role", "Name", where: WhereStatic("Description", "visible"))));
        await _db.GetCollection<SchemaDefinition>("SchemaDefinitions").UpdateOneAsync(
            d => d.ItemId == "sch-rolegrant", Builders<SchemaDefinition>.Update.Set(d => d.EditAccessLevel, SchemaAccessLevel.Custom));
        await _db.GetCollection<DataAccessPolicy>("DataAccessPolicys").InsertOneAsync(
            WithSchema(Rls(PolicyOperation.EDIT, Lookup("RoleName", "Role", "Name", where: WhereStatic("Description", "secret")))));
        TakeCommands();

        var update = await Resolver().ResolveUpdateAsync("RoleGrant", new BsonDocument(), new Dictionary<string, object?> { ["Resource"] = "y" });

        Assert.True(update.IsAccessGranted);
        // Both lookups ran (one per WHERE filter); the update filter is the EDIT rule's.
        Assert.Equal(2, TakeCommands().Count(c => c is ("aggregate", "Roles")));
        Assert.Equal(["g-hidden"], await RowIdsAsync("RoleGrants", update.Filter!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddBlocksDataAccess_RegistersTheLookupRunnerAndResolver(bool withLogging)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IDbContextProvider>());
        if (withLogging)
            services.AddLogging();
        services.AddBlocksDataAccess();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<DbContextAccessLookupRunner>(provider.GetRequiredService<IAccessLookupRunner>());
        Assert.NotNull(provider.GetRequiredService<AccessLookupResolver>());
        Assert.NotNull(provider.GetRequiredService<IDataAccessResolver>());
    }

    [Fact]
    public async Task Runner_ReturnsDistinctValues_FromThePrincipalsDatabase()
    {
        await SeedRoleAsync();
        var runner = new DbContextAccessLookupRunner(_contextProvider.Object, _principals.Object);

        var values = await runner.RunAsync(
            new AccessLookupQuery("Roles", "Description", true, new BsonDocument("IsDeleted", new BsonDocument("$ne", true)), 10), CancellationToken.None);

        Assert.Equal(["secret", "visible"], values.Select(v => v.AsString).Order().ToList());
        Assert.Single(await runner.RunAsync(new AccessLookupQuery("Roles", "Description", true, new BsonDocument(), 1), CancellationToken.None));
    }

    private static DataAccessPolicy WithSchema(DataAccessPolicy policy)
    {
        policy.SchemaId = "sch-rolegrant";
        return policy;
    }
}
