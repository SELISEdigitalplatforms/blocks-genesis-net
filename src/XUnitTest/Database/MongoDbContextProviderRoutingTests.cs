using Blocks.Genesis;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using System.Diagnostics;

namespace XUnitTest.Database;

// These tests inspect driver handles/settings without issuing database commands.
public sealed class MongoDbContextProviderRoutingTests : IDisposable
{
    private const string MainConnection = "mongodb://127.0.0.1:27017";
    private const string DevConnection = "mongodb://127.0.0.1:27018";
    private const string OtherConnection = "mongodb://127.0.0.1:27019";
    private readonly ActivitySource _activitySource = new(nameof(MongoDbContextProviderRoutingTests));
    private readonly Mock<ITenants> _tenants = new(MockBehavior.Strict);
    private readonly MongoDbContextProvider _provider;

    public MongoDbContextProviderRoutingTests()
    {
        _provider = new MongoDbContextProvider(
            NullLogger<MongoDbContextProvider>.Instance, _tenants.Object, _activitySource);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameDatabaseName_OnDifferentConnections_RemainsIsolated(bool refresh)
    {
        var main = _provider.GetDatabase(MainConnection, "shared_name", refresh);
        var dev = _provider.GetDatabase(DevConnection, "shared_name", refresh);
        var other = _provider.GetDatabase(OtherConnection, "shared_name", refresh);

        AssertTarget(main, "shared_name", 27017);
        AssertTarget(dev, "shared_name", 27018);
        AssertTarget(other, "shared_name", 27019);
        Assert.NotSame(main.Client, dev.Client);
        Assert.NotSame(dev.Client, other.Client);
        Assert.Same(main, _provider.GetDatabase(MainConnection, "shared_name", refresh));
        Assert.Same(dev, _provider.GetDatabase(DevConnection, "shared_name", refresh));
        Assert.Same(other, _provider.GetDatabase(OtherConnection, "shared_name", refresh));
    }

    [Fact]
    public void SameEndpoint_WithDifferentConnectionOptions_UsesDifferentClients()
    {
        var first = _provider.GetDatabase(MainConnection, "options_db");
        var second = _provider.GetDatabase(MainConnection + "/?maxPoolSize=99", "options_db");

        Assert.NotSame(first, second);
        Assert.NotSame(first.Client, second.Client);
        Assert.Equal(99, second.Client.Settings.MaxConnectionPoolSize);
        Assert.Same(first, _provider.GetDatabase(MainConnection, "options_db"));
    }

    [Fact]
    public void DifferentDatabases_OnSameConnection_ReuseClient()
    {
        var first = _provider.GetDatabase(DevConnection, "tenant_a_db");
        var second = _provider.GetDatabase(DevConnection, "tenant_b_db");

        Assert.NotSame(first, second);
        Assert.Same(first.Client, second.Client);
        AssertTarget(first, "tenant_a_db", 27018);
        AssertTarget(second, "tenant_b_db", 27018);
    }

    [Fact]
    public void TenantConnectionUpdate_RoutesNewCollectionLookupsToUpdatedConnection()
    {
        _tenants.SetupSequence(t => t.GetTenantDatabaseConnectionString("tenant-a"))
            .Returns(("tenant_db", MainConnection))
            .Returns(("tenant_db", DevConnection));

        var before = _provider.GetCollection<BsonDocument>("tenant-a", "Users");
        var after = _provider.GetCollection<BsonDocument>("tenant-a", "Users");

        AssertTarget(before.Database, "tenant_db", 27017);
        AssertTarget(after.Database, "tenant_db", 27018);
        Assert.Equal("Users", after.CollectionNamespace.CollectionName);
        Assert.Same(after.Database, _provider.GetDatabase(DevConnection, "tenant_db"));
        _tenants.Verify(t => t.GetTenantDatabaseConnectionString("tenant-a"), Times.Exactly(2));
    }

    [Fact]
    public void TenantDatabaseNameUpdate_UsesNewDatabaseAndReusesClient()
    {
        _tenants.SetupSequence(t => t.GetTenantDatabaseConnectionString("tenant-a"))
            .Returns(("before_db", DevConnection))
            .Returns(("after_db", DevConnection));

        var before = _provider.GetDatabase("tenant-a");
        var after = _provider.GetDatabase("tenant-a");

        AssertTarget(before, "before_db", 27018);
        AssertTarget(after, "after_db", 27018);
        Assert.Same(before.Client, after.Client);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("tenant_db", "")]
    [InlineData("tenant_db", "   ")]
    [InlineData("", DevConnection)]
    public void MissingUpdatedTenantRouting_DoesNotReusePreviouslyCachedDatabase(string? dbName, string? connection)
    {
        _tenants.SetupSequence(t => t.GetTenantDatabaseConnectionString("tenant-a"))
            .Returns(("tenant_db", MainConnection))
            .Returns((dbName, connection));
        _provider.GetDatabase("tenant-a");

        var error = Assert.Throws<InvalidOperationException>(() => _provider.GetDatabase("tenant-a"));

        Assert.IsType<KeyNotFoundException>(error.InnerException);
        _tenants.Verify(t => t.GetTenantDatabaseConnectionString("tenant-a"), Times.Exactly(2));
    }

    [Fact]
    public void TenantIdMatchingAnExplicitDatabaseName_DoesNotCollideWithItsCacheEntry()
    {
        var root = _provider.GetDatabase(MainConnection, "tenant-a");
        _tenants.Setup(t => t.GetTenantDatabaseConnectionString("tenant-a"))
            .Returns(("tenant_db", DevConnection));

        var tenant = _provider.GetDatabase("tenant-a");

        AssertTarget(tenant, "tenant_db", 27018);
        AssertTarget(root, "tenant-a", 27017);
        Assert.Same(root, _provider.GetDatabase(MainConnection, "tenant-a"));
    }

    [Fact]
    public async Task ConcurrentTenantLookups_KeepPlacementsSeparateAndReuseHandles()
    {
        var connections = new[] { MainConnection, DevConnection, OtherConnection };
        for (var group = 0; group < connections.Length; group++)
        {
            var connection = connections[group];
            var tenantId = $"tenant-{group}";
            _tenants.Setup(t => t.GetTenantDatabaseConnectionString(tenantId))
                .Returns(("shared_name", connection));
        }

        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 60).Select(async index =>
        {
            await start.Task;
            return (Group: index % 3, Database: _provider.GetDatabase($"tenant-{index % 3}"));
        }).ToArray();
        start.SetResult(true);

        var results = await Task.WhenAll(tasks);

        foreach (var result in results)
        {
            AssertTarget(result.Database, "shared_name", 27017 + result.Group);
            Assert.Same(_provider.GetDatabase(connections[result.Group], "shared_name"), result.Database);
        }
    }

    [Fact]
    public async Task ConcurrentDifferentDatabaseLookups_OnOneConnection_ReuseSingleClient()
    {
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 30).Select(async index =>
        {
            await start.Task;
            return _provider.GetDatabase(DevConnection, $"tenant_{index}");
        }).ToArray();
        start.SetResult(true);

        var databases = await Task.WhenAll(tasks);

        Assert.All(databases, db => Assert.Same(databases[0].Client, db.Client));
        Assert.Equal(30, databases.Select(db => db.DatabaseNamespace.DatabaseName).Distinct().Count());
    }

    private static void AssertTarget(IMongoDatabase database, string name, int port)
    {
        Assert.Equal(name, database.DatabaseNamespace.DatabaseName);
        var server = Assert.Single(database.Client.Settings.Servers);
        Assert.Equal("127.0.0.1", server.Host);
        Assert.Equal(port, server.Port);
    }

    public void Dispose() => _activitySource.Dispose();
}
