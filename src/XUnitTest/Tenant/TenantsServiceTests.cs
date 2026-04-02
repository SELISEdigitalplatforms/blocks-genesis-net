using Blocks.Genesis;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using MongoDB.Driver;
using StackExchange.Redis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

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
    public void GetTenantByID_ShouldReturnTenantFromL1MemoryCache_WhenPresent()
    {
        var sut = CreateSut();
        var memory = GetField<IMemoryCache>(sut, "_memoryCache");
        var tenant = CreateTenant("tenant-l1");
        memory.Set("tenant:tenant-l1", tenant, new MemoryCacheEntryOptions { Size = 1 });

        var result = sut.GetTenantByID("tenant-l1");

        Assert.Same(tenant, result);
    }

    [Fact]
    public void GetTenantByID_ShouldReturnTenantFromRedis_WhenL1Miss()
    {
        var cacheClient = new Mock<ICacheClient>();
        var redisTenant = CreateTenant("tenant-l2");
        cacheClient.Setup(c => c.GetStringValue("tenant:tenant-l2"))
            .Returns(JsonSerializer.Serialize(redisTenant));

        var sut = CreateSut(cacheClient: cacheClient.Object);

        var result = sut.GetTenantByID("tenant-l2");

        Assert.NotNull(result);
        Assert.Equal("tenant-l2", result!.TenantId);
        cacheClient.Verify(c => c.GetStringValue("tenant:tenant-l2"), Times.Once);
    }


    [Fact]
    public void GetTenantByApplicationDomain_ShouldReturnTenantFromL1_WhenPresent()
    {
        var sut = CreateSut();
        var memory = GetField<IMemoryCache>(sut, "_memoryCache");
        memory.Set(
            "tenant:domain:https://app.local",
            CreateTenant("tenant-1", "db-main", "mongo-main"),
            new MemoryCacheEntryOptions { Size = 1 });

        var result = ((ITenantLookup)sut).GetTenantByApplicationDomain("app.local");

        Assert.NotNull(result);
        Assert.Equal("tenant-1", result!.TenantId);
    }

    [Fact]
    public void HandleTenantInvalidation_ShouldRemoveTenantFromL1()
    {
        var sut = CreateSut();
        var memory = GetField<IMemoryCache>(sut, "_memoryCache");
        memory.Set(
            "tenant:tenant-invalidate",
            CreateTenant("tenant-invalidate"),
            new MemoryCacheEntryOptions { Size = 1 });

        InvokeHandleTenantInvalidation(sut, "tenant:invalidate", "tenant-invalidate");

        Assert.False(memory.TryGetValue("tenant:tenant-invalidate", out _));
    }

    [Fact]
    public void HandleTenantInvalidation_ShouldRemoveTrackedDomainCache_WhenOnlyDomainEntryExists()
    {
        var sut = CreateSut();
        var memory = GetField<IMemoryCache>(sut, "_memoryCache");
        var tenant = CreateTenant("tenant-domain");
        var domainKey = "tenant:domain:https://app.local";
        var trackedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { domainKey };

        memory.Set(domainKey, tenant, new MemoryCacheEntryOptions { Size = 1 });
        memory.Set("tenant:domains:tenant-domain", trackedKeys, new MemoryCacheEntryOptions { Size = 1 });

        InvokeHandleTenantInvalidation(sut, "tenant:invalidate", "tenant-domain");

        Assert.False(memory.TryGetValue(domainKey, out _));
        Assert.False(memory.TryGetValue("tenant:domains:tenant-domain", out _));
    }

    [Fact]
    public void Dispose_ShouldUnsubscribeFromInvalidationChannel_WhenSubscribed()
    {
        var cacheClient = new Mock<ICacheClient>();
        cacheClient.Setup(c => c.UnsubscribeAsync("tenant:invalidate")).Returns(Task.CompletedTask);

        var sut = CreateSut(cacheClient: cacheClient.Object);
        SetField(sut, "_isSubscribed", true);

        sut.Dispose();

        cacheClient.Verify(c => c.UnsubscribeAsync("tenant:invalidate"), Times.Once);
    }

    private static Tenants CreateSut(ICacheClient? cacheClient = null)
    {
        var instance = (Tenants)RuntimeHelpers.GetUninitializedObject(typeof(Tenants));

        SetField(instance, "_logger", Mock.Of<ILogger<Tenants>>());
        SetField(instance, "_cacheClient", cacheClient ?? Mock.Of<ICacheClient>());
        SetField(instance, "_memoryCache", new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 }));
        SetField(instance, "_rootConnectionString", "mongodb://localhost:27017");
        SetField(instance, "_rootDatabaseName", "root");
        SetField(instance, "_tenantInvalidationChannel", "tenant:invalidate");
        SetField(instance, "_traceConnectionString", string.Empty);
        SetField(instance, "_isSubscribed", false);
        SetField(instance, "_disposed", false);

        return instance;
    }

    private static void InvokeHandleTenantInvalidation(Tenants sut, string channel, string message)
    {
        var method = typeof(Tenants).GetMethod("HandleTenantInvalidation", BindingFlags.NonPublic | BindingFlags.Instance)!;
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
}
