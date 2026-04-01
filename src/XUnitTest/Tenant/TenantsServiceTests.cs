using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using Moq;
using MongoDB.Driver;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace XUnitTest.Tenant;

public class TenantsServiceTests
{
    [Fact]
    public void GetTenantByID_ShouldReturnNull_WhenTenantIdIsEmpty()
    {
        var sut = CreateSut();

        Assert.Null(sut.GetTenantByID(string.Empty));
        Assert.Null(sut.GetTenantByID("   "));
    }

    [Fact]
    public void GetTenantByID_ShouldReturnTenantFromCache_WhenPresent()
    {
        var sut = CreateSut();
        var cache = GetField<ConcurrentDictionary<string, Blocks.Genesis.Tenant>>(sut, "_tenantCache");
        var tenant = CreateTenant("tenant-1");
        cache["tenant-1"] = tenant;

        var result = sut.GetTenantByID("tenant-1");

        Assert.Same(tenant, result);
    }

    [Fact]
    public void GetTenantByID_ShouldReturnNull_WhenDbThrowsOnCacheMiss()
    {
        var sut = CreateSut();
        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<Blocks.Genesis.Tenant>(It.IsAny<string>(), It.IsAny<MongoCollectionSettings>()))
            .Throws(new Exception("db-error"));
        SetField(sut, "_database", database.Object);

        var result = sut.GetTenantByID("tenant-miss");

        Assert.Null(result);
    }

    [Fact]
    public void GetTenantByID_ShouldLoadFromDbAndCache_WhenCacheMiss()
    {
        var sut = CreateSut();
        var database = new Mock<IMongoDatabase>();
        var collection = new Mock<IMongoCollection<Blocks.Genesis.Tenant>>();

        collection
            .Setup(c => c.FindSync(
                It.IsAny<FilterDefinition<Blocks.Genesis.Tenant>>(),
                It.IsAny<FindOptions<Blocks.Genesis.Tenant, Blocks.Genesis.Tenant>>(),
                It.IsAny<CancellationToken>()))
            .Returns(CreateCursorWithFirst(CreateTenant("tenant-db")).Object);

        database
            .Setup(d => d.GetCollection<Blocks.Genesis.Tenant>(It.IsAny<string>(), It.IsAny<MongoCollectionSettings>()))
            .Returns(collection.Object);
        SetField(sut, "_database", database.Object);

        var result = sut.GetTenantByID("tenant-db");

        Assert.NotNull(result);
        Assert.Equal("tenant-db", result!.TenantId);

        var cache = GetField<ConcurrentDictionary<string, Blocks.Genesis.Tenant>>(sut, "_tenantCache");
        Assert.True(cache.ContainsKey("tenant-db"));
    }

    [Fact]
    public void GetTenantDatabaseConnectionStrings_ShouldMapFromCache()
    {
        var sut = CreateSut();
        var cache = GetField<ConcurrentDictionary<string, Blocks.Genesis.Tenant>>(sut, "_tenantCache");
        cache["t1"] = CreateTenant("t1", dbName: "db1", connection: "conn1");
        cache["t2"] = CreateTenant("t2", dbName: "db2", connection: "conn2");

        var result = sut.GetTenantDatabaseConnectionStrings();

        Assert.Equal(2, result.Count);
        Assert.Equal(("db1", "conn1"), result["t1"]);
        Assert.Equal(("db2", "conn2"), result["t2"]);
    }

    [Fact]
    public void GetTenantDatabaseConnectionString_ShouldReturnNullTuple_WhenTenantIdIsEmpty()
    {
        var sut = CreateSut();

        var result = sut.GetTenantDatabaseConnectionString(" ");

        Assert.Equal((null, null), result);
    }

    [Fact]
    public void GetTenantDatabaseConnectionString_ShouldReturnTenantValues_WhenFound()
    {
        var sut = CreateSut();
        var cache = GetField<ConcurrentDictionary<string, Blocks.Genesis.Tenant>>(sut, "_tenantCache");
        cache["tenant-1"] = CreateTenant("tenant-1", dbName: "db-main", connection: "mongo-main");

        var result = sut.GetTenantDatabaseConnectionString("tenant-1");

        Assert.Equal(("db-main", "mongo-main"), result);
    }

    [Fact]
    public void GetTenantTokenValidationParameter_ShouldReturnNull_WhenTenantIdIsEmpty()
    {
        var sut = CreateSut();

        var result = sut.GetTenantTokenValidationParameter(string.Empty);

        Assert.Null(result);
    }

    [Fact]
    public void GetTenantTokenValidationParameter_ShouldReturnJwtParams_WhenFound()
    {
        var sut = CreateSut();
        var cache = GetField<ConcurrentDictionary<string, Blocks.Genesis.Tenant>>(sut, "_tenantCache");
        var tenant = CreateTenant("tenant-1");
        cache["tenant-1"] = tenant;

        var result = sut.GetTenantTokenValidationParameter("tenant-1");

        Assert.Same(tenant.JwtTokenParameters, result);
    }

    [Fact]
    public async Task UpdateTenantVersionAsync_ShouldPublish_WhenCacheSetSucceeds()
    {
        var sut = CreateSut();
        var cacheClient = new Mock<ICacheClient>();
        cacheClient.Setup(c => c.AddStringValueAsync("tenant::version", It.IsAny<string>())).ReturnsAsync(true);
        cacheClient.Setup(c => c.PublishAsync("tenant::updates", It.IsAny<string>())).ReturnsAsync(1);
        SetField(sut, "_cacheClient", cacheClient.Object);

        await sut.UpdateTenantVersionAsync();

        cacheClient.Verify(c => c.AddStringValueAsync("tenant::version", It.IsAny<string>()), Times.Once);
        cacheClient.Verify(c => c.PublishAsync("tenant::updates", It.IsAny<string>()), Times.Once);

        var tenantVersion = GetField<string>(sut, "_tenantVersion");
        Assert.False(string.IsNullOrWhiteSpace(tenantVersion));
    }

    [Fact]
    public async Task UpdateTenantVersionAsync_ShouldNotPublish_WhenCacheSetFails()
    {
        var sut = CreateSut();
        var cacheClient = new Mock<ICacheClient>();
        cacheClient.Setup(c => c.AddStringValueAsync("tenant::version", It.IsAny<string>())).ReturnsAsync(false);
        SetField(sut, "_cacheClient", cacheClient.Object);

        await sut.UpdateTenantVersionAsync();

        cacheClient.Verify(c => c.PublishAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UpdateTenantVersionAsync_ShouldCatchExceptions()
    {
        var sut = CreateSut();
        var cacheClient = new Mock<ICacheClient>();
        cacheClient.Setup(c => c.AddStringValueAsync("tenant::version", It.IsAny<string>())).ThrowsAsync(new Exception("cache-fail"));
        SetField(sut, "_cacheClient", cacheClient.Object);

        var exception = await Record.ExceptionAsync(() => sut.UpdateTenantVersionAsync());

        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_ShouldUnsubscribe_WhenSubscribed()
    {
        var sut = CreateSut();
        var cacheClient = new Mock<ICacheClient>();
        cacheClient.Setup(c => c.UnsubscribeAsync("tenant::updates")).Returns(Task.CompletedTask);
        SetField(sut, "_cacheClient", cacheClient.Object);
        SetField(sut, "_isSubscribed", true);

        sut.Dispose();

        cacheClient.Verify(c => c.UnsubscribeAsync("tenant::updates"), Times.Once);
        Assert.True(GetField<bool>(sut, "_disposed"));
    }

    [Fact]
    public void Dispose_ShouldBeIdempotent_WhenAlreadyDisposed()
    {
        var sut = CreateSut();
        var cacheClient = new Mock<ICacheClient>();
        SetField(sut, "_cacheClient", cacheClient.Object);
        SetField(sut, "_disposed", true);

        sut.Dispose();

        cacheClient.Verify(c => c.UnsubscribeAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void GetTenantByApplicationDomain_ShouldReturnNull_WhenAppNameIsEmpty()
    {
        var sut = CreateSut();

        var result = sut.GetTenantByApplicationDomain(" ");

        Assert.Null(result);
    }

    [Fact]
    public void GetTenantByApplicationDomain_ShouldReturnNull_WhenDbThrows()
    {
        var sut = CreateSut();
        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<Blocks.Genesis.Tenant>(It.IsAny<string>(), It.IsAny<MongoCollectionSettings>()))
            .Throws(new Exception("db-fail"));
        SetField(sut, "_database", database.Object);

        var result = sut.GetTenantByApplicationDomain("acme.local");

        Assert.Null(result);
    }

    [Fact]
    public void GetTenantByApplicationDomain_ShouldReturnTenant_WhenDbHasMatch()
    {
        var sut = CreateSut();
        var database = new Mock<IMongoDatabase>();
        var collection = new Mock<IMongoCollection<Blocks.Genesis.Tenant>>();
        var tenant = CreateTenant("tenant-1");

        collection
            .Setup(c => c.FindSync(
                It.IsAny<FilterDefinition<Blocks.Genesis.Tenant>>(),
                It.IsAny<FindOptions<Blocks.Genesis.Tenant, Blocks.Genesis.Tenant>>(),
                It.IsAny<CancellationToken>()))
            .Returns(CreateCursorWithFirst(tenant).Object);

        database
            .Setup(d => d.GetCollection<Blocks.Genesis.Tenant>(It.IsAny<string>(), It.IsAny<MongoCollectionSettings>()))
            .Returns(collection.Object);
        SetField(sut, "_database", database.Object);

        var result = sut.GetTenantByApplicationDomain("acme.local");

        Assert.NotNull(result);
        Assert.Equal("tenant-1", result!.TenantId);
    }

    [Fact]
    public async Task SubscribeToTenantUpdates_ShouldSubscribeAndMarkFlag()
    {
        var sut = CreateSut();
        var cacheClient = new Mock<ICacheClient>();
        cacheClient
            .Setup(c => c.SubscribeAsync("tenant::updates", It.IsAny<Action<RedisChannel, RedisValue>>()))
            .Returns(Task.CompletedTask);
        SetField(sut, "_cacheClient", cacheClient.Object);

        await InvokePrivateSubscribeToTenantUpdates(sut);

        cacheClient.Verify(c => c.SubscribeAsync("tenant::updates", It.IsAny<Action<RedisChannel, RedisValue>>()), Times.Once);
        Assert.True(GetField<bool>(sut, "_isSubscribed"));
    }

    [Fact]
    public async Task SubscribeToTenantUpdates_ShouldReturn_WhenAlreadySubscribed()
    {
        var sut = CreateSut();
        var cacheClient = new Mock<ICacheClient>();
        SetField(sut, "_cacheClient", cacheClient.Object);
        SetField(sut, "_isSubscribed", true);

        await InvokePrivateSubscribeToTenantUpdates(sut);

        cacheClient.Verify(c => c.SubscribeAsync(It.IsAny<string>(), It.IsAny<Action<RedisChannel, RedisValue>>()), Times.Never);
    }

    [Fact]
    public async Task SubscribeToTenantUpdates_ShouldCatchSubscribeErrors()
    {
        var sut = CreateSut();
        var cacheClient = new Mock<ICacheClient>();
        cacheClient
            .Setup(c => c.SubscribeAsync("tenant::updates", It.IsAny<Action<RedisChannel, RedisValue>>()))
            .ThrowsAsync(new Exception("sub-fail"));
        SetField(sut, "_cacheClient", cacheClient.Object);

        var exception = await Record.ExceptionAsync(() => InvokePrivateSubscribeToTenantUpdates(sut));

        Assert.Null(exception);
        Assert.False(GetField<bool>(sut, "_isSubscribed"));
    }

    [Fact]
    public void ReloadTenants_ShouldPopulateCacheFromDatabase()
    {
        var sut = CreateSut();
        var database = new Mock<IMongoDatabase>();
        var collection = new Mock<IMongoCollection<Blocks.Genesis.Tenant>>();

        var tenant1 = CreateTenant("t1");
        tenant1.CreatedDate = DateTime.UtcNow.AddDays(-2);
        var tenant2 = CreateTenant("t2");
        tenant2.CreatedDate = DateTime.UtcNow.AddDays(-3);
        var tenants = new[] { tenant1, tenant2 };

        collection
            .Setup(c => c.FindSync(
                It.IsAny<FilterDefinition<Blocks.Genesis.Tenant>>(),
                It.IsAny<FindOptions<Blocks.Genesis.Tenant, Blocks.Genesis.Tenant>>(),
                It.IsAny<CancellationToken>()))
            .Returns(CreateCursorWithMany(tenants).Object);

        database
            .Setup(d => d.GetCollection<Blocks.Genesis.Tenant>(It.IsAny<string>(), It.IsAny<MongoCollectionSettings>()))
            .Returns(collection.Object);
        SetField(sut, "_database", database.Object);

        InvokePrivateReloadTenants(sut);

        var cache = GetField<ConcurrentDictionary<string, Blocks.Genesis.Tenant>>(sut, "_tenantCache");
        Assert.Equal(2, cache.Count);
        Assert.True(cache.ContainsKey("t1"));
        Assert.True(cache.ContainsKey("t2"));
    }

    [Fact]
    public void Dispose_ShouldCatchUnsubscribeExceptions()
    {
        var sut = CreateSut();
        var cacheClient = new Mock<ICacheClient>();
        cacheClient.Setup(c => c.UnsubscribeAsync("tenant::updates")).ThrowsAsync(new Exception("unsub-fail"));
        SetField(sut, "_cacheClient", cacheClient.Object);
        SetField(sut, "_isSubscribed", true);

        var exception = Record.Exception(() => sut.Dispose());

        Assert.Null(exception);
        Assert.True(GetField<bool>(sut, "_disposed"));
    }

    [Fact]
    public void HandleTenantUpdate_ShouldSkipReload_WhenVersionIsUnchanged()
    {
        var sut = CreateSut();
        SetField(sut, "_tenantVersion", "v1");

        InvokePrivateHandleTenantUpdate(sut, "tenant::updates", "v1");

        Assert.Equal("v1", GetField<string>(sut, "_tenantVersion"));
    }

    [Fact]
    public void HandleTenantUpdate_ShouldUpdateVersion_WhenVersionChanged()
    {
        var sut = CreateSut();
        SetField(sut, "_tenantVersion", "old-version");

        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<Blocks.Genesis.Tenant>(It.IsAny<string>(), It.IsAny<MongoCollectionSettings>()))
            .Throws(new Exception("reload-fail"));
        SetField(sut, "_database", database.Object);

        InvokePrivateHandleTenantUpdate(sut, "tenant::updates", "new-version");

        Assert.Equal("new-version", GetField<string>(sut, "_tenantVersion"));
    }

    private static Tenants CreateSut()
    {
        var instance = (Tenants)RuntimeHelpers.GetUninitializedObject(typeof(Tenants));

        SetField(instance, "_logger", Mock.Of<ILogger<Tenants>>());
        SetField(instance, "_blocksSecret", Mock.Of<IBlocksSecret>());
        SetField(instance, "_cacheClient", Mock.Of<ICacheClient>());
        SetField(instance, "_database", Mock.Of<IMongoDatabase>());
        SetField(instance, "_tenantVersionKey", "tenant::version");
        SetField(instance, "_tenantUpdateChannel", "tenant::updates");
        SetField(instance, "_tenantVersion", string.Empty);
        SetField(instance, "_isSubscribed", false);
        SetField(instance, "_disposed", false);
        SetField(instance, "_tenantCache", new ConcurrentDictionary<string, Blocks.Genesis.Tenant>());

        return instance;
    }

    private static async Task InvokePrivateSubscribeToTenantUpdates(Tenants sut)
    {
        var method = typeof(Tenants).GetMethod("SubscribeToTenantUpdates", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)method.Invoke(sut, null)!;
        await task;
    }

    private static void InvokePrivateReloadTenants(Tenants sut)
    {
        var method = typeof(Tenants).GetMethod("ReloadTenants", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(sut, [false]);
    }

    private static void InvokePrivateHandleTenantUpdate(Tenants sut, string channel, string message)
    {
        var method = typeof(Tenants).GetMethod("HandleTenantUpdate", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(sut, [new RedisChannel(channel, RedisChannel.PatternMode.Literal), (RedisValue)message]);
    }

    private static void SetField<T>(object target, string fieldName, T value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(target, value);
    }

    private static T GetField<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (T)field.GetValue(target)!;
    }

    private static Blocks.Genesis.Tenant CreateTenant(string tenantId, string dbName = "db", string connection = "conn")
    {
        return new Blocks.Genesis.Tenant
        {
            TenantId = tenantId,
            DBName = dbName,
            DbConnectionString = connection,
            ApplicationDomain = "https://app.local",
            JwtTokenParameters = new JwtTokenParameters
            {
                Issuer = "issuer",
                Subject = "subject",
                Audiences = ["aud"],
                PublicCertificatePath = "path",
                PublicCertificatePassword = "pwd",
                PrivateCertificatePassword = "private",
                IssueDate = DateTime.UtcNow
            }
        };
    }

    private static Mock<IAsyncCursor<Blocks.Genesis.Tenant>> CreateCursorWithFirst(Blocks.Genesis.Tenant? tenant)
    {
        var items = tenant == null ? Array.Empty<Blocks.Genesis.Tenant>() : new[] { tenant };
        return CreateCursorWithMany(items);
    }

    private static Mock<IAsyncCursor<Blocks.Genesis.Tenant>> CreateCursorWithMany(IEnumerable<Blocks.Genesis.Tenant> tenants)
    {
        var list = tenants.ToList();
        var cursor = new Mock<IAsyncCursor<Blocks.Genesis.Tenant>>();
        cursor.SetupSequence(c => c.MoveNext(It.IsAny<CancellationToken>()))
            .Returns(true)
            .Returns(false);
        cursor.SetupGet(c => c.Current).Returns(list);
        return cursor;
    }
}