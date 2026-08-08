using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Moq;
using System.Reflection;

namespace XUnitTest.Tenant;

public class TenantsCoverageTests
{
    private const string MongoConnectionString = "mongodb://127.0.0.1:27017";
    private const string UnreachableConnectionString = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=200&connectTimeoutMS=200";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetTenantByID_ShouldReturnNull_ForMissingTenantId(string? tenantId)
    {
        using var tenants = CreateTenants($"tenants-cov-{Guid.NewGuid():N}", out _, out _);

        Assert.Null(tenants.GetTenantByID(tenantId!));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetTenantByID_ShouldLoadFromDatabase_ThenServeFromCache()
    {
        var databaseName = $"tenants-cov-{Guid.NewGuid():N}";
        var client = new MongoClient(MongoConnectionString);
        try
        {
            var tenant = MakeTenant("tenant-db");
            await client.GetDatabase(databaseName).GetCollection<Blocks.Genesis.Tenant>("Tenants").InsertOneAsync(tenant);

            using var tenants = CreateTenants(databaseName, out _, out _);

            var loaded = tenants.GetTenantByID("tenant-db");
            Assert.NotNull(loaded);
            Assert.Equal("tenant-db", loaded!.TenantId);

            // Second lookup is served from the in-memory cache.
            var cached = tenants.GetTenantByID("tenant-db");
            Assert.NotNull(cached);

            var connections = tenants.GetTenantDatabaseConnectionStrings();
            Assert.True(connections.ContainsKey("tenant-db"));

            var (dbName, connection) = tenants.GetTenantDatabaseConnectionString("tenant-db");
            Assert.Equal(loaded.DBName, dbName);
            Assert.Equal(loaded.DbConnectionString, connection);

            var parameters = tenants.GetTenantTokenValidationParameter("tenant-db");
            Assert.NotNull(parameters);
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void GetTenantByID_ShouldReturnNull_WhenTenantDoesNotExist()
    {
        var databaseName = $"tenants-cov-{Guid.NewGuid():N}";
        var client = new MongoClient(MongoConnectionString);
        try
        {
            using var tenants = CreateTenants(databaseName, out _, out _);

            Assert.Null(tenants.GetTenantByID("missing-tenant"));
        }
        finally
        {
            client.DropDatabase(databaseName);
        }
    }

    [Fact]
    public void GetTenantByID_ShouldReturnNull_WhenDatabaseIsUnreachable()
    {
        using var tenants = CreateTenants($"tenants-cov-{Guid.NewGuid():N}", out _, out _, UnreachableConnectionString);

        Assert.Null(tenants.GetTenantByID("any-tenant"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetTenantDatabaseConnectionString_ShouldReturnNulls_ForMissingTenantId(string? tenantId)
    {
        using var tenants = CreateTenants($"tenants-cov-{Guid.NewGuid():N}", out _, out _);

        var (dbName, connection) = tenants.GetTenantDatabaseConnectionString(tenantId!);

        Assert.Null(dbName);
        Assert.Null(connection);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void GetTenantDatabaseConnectionString_ShouldReturnNulls_WhenTenantMissing()
    {
        var databaseName = $"tenants-cov-{Guid.NewGuid():N}";
        var client = new MongoClient(MongoConnectionString);
        try
        {
            using var tenants = CreateTenants(databaseName, out _, out _);

            var (dbName, connection) = tenants.GetTenantDatabaseConnectionString("nope");

            Assert.Null(dbName);
            Assert.Null(connection);
        }
        finally
        {
            client.DropDatabase(databaseName);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void GetTenantTokenValidationParameter_ShouldReturnNull_ForMissingTenantId(string? tenantId)
    {
        using var tenants = CreateTenants($"tenants-cov-{Guid.NewGuid():N}", out _, out _);

        Assert.Null(tenants.GetTenantTokenValidationParameter(tenantId!));
    }

    [Fact]
    public async Task UpdateTenantVersionAsync_ShouldThrow_WhenUpdateIsNull()
    {
        using var tenants = CreateTenants($"tenants-cov-{Guid.NewGuid():N}", out _, out _);

        await Assert.ThrowsAsync<ArgumentNullException>(() => tenants.UpdateTenantVersionAsync(null!));
    }

    [Fact]
    public async Task UpdateTenantVersionAsync_ShouldSkipPublish_WhenActionIsInvalid()
    {
        using var tenants = CreateTenants($"tenants-cov-{Guid.NewGuid():N}", out var cacheClient, out _);

        await tenants.UpdateTenantVersionAsync(new TenantCacheUpdateMessage { Action = "unknown" });

        cacheClient.Verify(c => c.PublishAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UpdateTenantVersionAsync_ShouldPublishUpsert_WhenTenantProvided()
    {
        using var tenants = CreateTenants($"tenants-cov-{Guid.NewGuid():N}", out var cacheClient, out _);

        await tenants.UpdateTenantVersionAsync(new TenantCacheUpdateMessage
        {
            Action = " Upsert ",
            Tenant = MakeTenant("tenant-upsert")
        });

        cacheClient.Verify(c => c.PublishAsync("tenant::updates", It.Is<string>(m => m.Contains("tenant-upsert"))), Times.Once);
    }

    [Fact]
    public async Task UpdateTenantVersionAsync_ShouldPublishRemove_WhenTenantIdProvided()
    {
        using var tenants = CreateTenants($"tenants-cov-{Guid.NewGuid():N}", out var cacheClient, out _);

        await tenants.UpdateTenantVersionAsync(new TenantCacheUpdateMessage
        {
            Action = "REMOVE",
            TenantId = "tenant-remove"
        });

        cacheClient.Verify(c => c.PublishAsync("tenant::updates", It.Is<string>(m => m.Contains("tenant-remove"))), Times.Once);
    }

    [Fact]
    public async Task UpdateTenantVersionAsync_ShouldSkipPublish_WhenRemoveHasNoTenantId()
    {
        using var tenants = CreateTenants($"tenants-cov-{Guid.NewGuid():N}", out var cacheClient, out _);

        await tenants.UpdateTenantVersionAsync(new TenantCacheUpdateMessage { Action = "remove" });

        cacheClient.Verify(c => c.PublishAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UpdateTenantVersionAsync_ShouldSwallowPublishFailures()
    {
        using var tenants = CreateTenants($"tenants-cov-{Guid.NewGuid():N}", out var cacheClient, out _);
        cacheClient.Setup(c => c.PublishAsync(It.IsAny<string>(), It.IsAny<string>()))
                   .ThrowsAsync(new InvalidOperationException("publish down"));

        var exception = await Record.ExceptionAsync(() => tenants.UpdateTenantVersionAsync(new TenantCacheUpdateMessage
        {
            Action = "upsert",
            Tenant = MakeTenant("tenant-fail")
        }));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetTenantByApplicationDomain_ShouldReturnNull_ForMissingAppName(string? appName)
    {
        using var tenants = CreateTenants($"tenants-cov-{Guid.NewGuid():N}", out _, out _);

        Assert.Null(tenants.GetTenantByApplicationDomain(appName!));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetTenantByApplicationDomain_ShouldFindTenantInDatabase_ThenServeFromCache()
    {
        var databaseName = $"tenants-cov-{Guid.NewGuid():N}";
        var client = new MongoClient(MongoConnectionString);
        try
        {
            var tenant = MakeTenant("tenant-domain");
            tenant.Applications = [new Applications { Domain = "https://app-cov.local" }];
            await client.GetDatabase(databaseName).GetCollection<Blocks.Genesis.Tenant>("Tenants").InsertOneAsync(tenant);

            using var tenants = CreateTenants(databaseName, out _, out _);

            var fromDb = tenants.GetTenantByApplicationDomain("app-cov.local");
            Assert.NotNull(fromDb);
            Assert.Equal("tenant-domain", fromDb!.TenantId);

            // Now cached; the cached branch matches by normalized domain.
            var fromCache = tenants.GetTenantByApplicationDomain("app-cov.local");
            Assert.NotNull(fromCache);

            Assert.Null(tenants.GetTenantByApplicationDomain("unknown-app.local"));
        }
        finally
        {
            await client.DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public void GetTenantByApplicationDomain_ShouldReturnNull_WhenDatabaseIsUnreachable()
    {
        using var tenants = CreateTenants($"tenants-cov-{Guid.NewGuid():N}", out _, out _, UnreachableConnectionString);

        Assert.Null(tenants.GetTenantByApplicationDomain("app.local"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task EnsureTraceCollectionExistsAsync_ShouldCreateTraceCollection_ForRecentTenant()
    {
        var tenantId = $"trace-tenant-{Guid.NewGuid():N}";
        using var tenants = CreateTenants($"tenants-cov-{Guid.NewGuid():N}", out _, out var secret);
        secret.SetupGet(s => s.TraceConnectionString).Returns(MongoConnectionString);

        var tenant = MakeTenant(tenantId);
        tenant.CreatedDate = DateTime.UtcNow;

        var traceDatabase = new MongoClient(MongoConnectionString).GetDatabase(LmtConfiguration.TraceDatabaseName);
        try
        {
            await InvokeEnsureTraceCollection(tenants, tenant);

            var filter = new MongoDB.Bson.BsonDocument("name", tenantId);
            Assert.True(traceDatabase.ListCollectionNames(new ListCollectionNamesOptions { Filter = filter }).Any());
        }
        finally
        {
            traceDatabase.DropCollection(tenantId);
        }
    }

    [Fact]
    public async Task EnsureTraceCollectionExistsAsync_ShouldSkip_ForOldTenant()
    {
        using var tenants = CreateTenants($"tenants-cov-{Guid.NewGuid():N}", out _, out var secret);
        var tenant = MakeTenant("old-tenant");
        tenant.CreatedDate = DateTime.UtcNow.AddDays(-2);

        await InvokeEnsureTraceCollection(tenants, tenant);

        secret.VerifyGet(s => s.TraceConnectionString, Times.Never);
    }

    [Fact]
    public async Task EnsureTraceCollectionExistsAsync_ShouldSwallowErrors_WhenSecretAccessFails()
    {
        using var tenants = CreateTenants($"tenants-cov-{Guid.NewGuid():N}", out _, out var secret);
        secret.SetupGet(s => s.TraceConnectionString).Throws(new InvalidOperationException("no secret"));

        var tenant = MakeTenant("boom-tenant");
        tenant.CreatedDate = DateTime.UtcNow;

        var exception = await Record.ExceptionAsync(() => InvokeEnsureTraceCollection(tenants, tenant));

        Assert.Null(exception);
    }

    private static Tenants CreateTenants(
        string databaseName,
        out Mock<ICacheClient> cacheClient,
        out Mock<IBlocksSecret> secret,
        string connectionString = MongoConnectionString)
    {
        cacheClient = new Mock<ICacheClient>();
        cacheClient.Setup(c => c.SubscribeAsync(It.IsAny<string>(), It.IsAny<Action<StackExchange.Redis.RedisChannel, StackExchange.Redis.RedisValue>>()))
                   .Returns(Task.CompletedTask);
        cacheClient.Setup(c => c.UnsubscribeAsync(It.IsAny<string>())).Returns(Task.CompletedTask);

        secret = new Mock<IBlocksSecret>();
        secret.SetupGet(s => s.DatabaseConnectionString).Returns(connectionString);
        secret.SetupGet(s => s.RootDatabaseName).Returns(databaseName);

        return new Tenants(new Mock<ILogger<Tenants>>().Object, secret.Object, cacheClient.Object);
    }

    private static Blocks.Genesis.Tenant MakeTenant(string tenantId)
    {
        return new Blocks.Genesis.Tenant
        {
            TenantId = tenantId,
            DbConnectionString = MongoConnectionString,
            JwtTokenParameters = new JwtTokenParameters
            {
                Issuer = "issuer", Subject = "subject", Audiences = [],
                PublicCertificatePath = "path", PublicCertificatePassword = string.Empty,
                PrivateCertificatePassword = string.Empty, IssueDate = DateTime.UtcNow
            }
        };
    }

    private static async Task InvokeEnsureTraceCollection(Tenants tenants, Blocks.Genesis.Tenant tenant)
    {
        var method = typeof(Tenants).GetMethod("EnsureTraceCollectionExistsAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        await (Task)method!.Invoke(tenants, [tenant])!;
    }
}
