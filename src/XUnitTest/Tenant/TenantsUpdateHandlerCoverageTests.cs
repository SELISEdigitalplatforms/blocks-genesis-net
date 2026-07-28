using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using System.Reflection;
using System.Text.Json;

namespace XUnitTest.Tenant;

public class TenantsUpdateHandlerCoverageTests
{
    private const string MongoConnectionString = "mongodb://127.0.0.1:27017";

    [Fact]
    public void HandleTenantUpdate_ShouldSkip_WhenNormalizationRejectsPayload()
    {
        using var tenants = CreateTenants(out var handler);

        // Valid JSON, but the action is unknown so normalization returns null.
        handler(RedisChannel.Literal("tenant::updates"), JsonSerializer.Serialize(new TenantCacheUpdateMessage { Action = "bogus" }));
        // Upsert without a tenant payload is also rejected by normalization.
        handler(RedisChannel.Literal("tenant::updates"), JsonSerializer.Serialize(new TenantCacheUpdateMessage { Action = "upsert" }));
        // A JSON literal null deserializes to null and is rejected by the parser.
        handler(RedisChannel.Literal("tenant::updates"), "null");

        Assert.Empty(GetCache(tenants));
    }

    [Fact]
    public void HandleTenantUpdate_ShouldRemoveCachedTenant_OnRemoveMessage()
    {
        using var tenants = CreateTenants(out var handler);
        GetCache(tenants)["tenant-r"] = MakeTenant("tenant-r");

        handler(RedisChannel.Literal("tenant::updates"), JsonSerializer.Serialize(new TenantCacheUpdateMessage
        {
            Action = "remove",
            TenantId = "tenant-r"
        }));

        Assert.Empty(GetCache(tenants));
    }

    [Fact]
    public void HandleTenantUpdate_ShouldEvictTenant_WhenUpsertedTenantIsDisabled()
    {
        using var tenants = CreateTenants(out var handler);
        GetCache(tenants)["tenant-d"] = MakeTenant("tenant-d");

        var disabled = MakeTenant("tenant-d");
        disabled.IsDisabled = true;
        handler(RedisChannel.Literal("tenant::updates"), JsonSerializer.Serialize(new TenantCacheUpdateMessage
        {
            Action = "upsert",
            Tenant = disabled
        }));

        Assert.Empty(GetCache(tenants));
    }

    [Fact]
    public void HandleTenantUpdate_ShouldCacheNewTenant_AndUpdateExistingTenant()
    {
        using var tenants = CreateTenants(out var handler);

        // New tenant: cached and the trace-collection kickoff branch runs
        // (CreatedDate is old, so it returns before touching Mongo).
        handler(RedisChannel.Literal("tenant::updates"), JsonSerializer.Serialize(new TenantCacheUpdateMessage
        {
            Action = "upsert",
            Tenant = MakeTenant("tenant-n")
        }));
        Assert.True(GetCache(tenants).ContainsKey("tenant-n"));

        // Existing tenant: updated without the new-tenant branch.
        handler(RedisChannel.Literal("tenant::updates"), JsonSerializer.Serialize(new TenantCacheUpdateMessage
        {
            Action = "upsert",
            Tenant = MakeTenant("tenant-n")
        }));
        Assert.True(GetCache(tenants).ContainsKey("tenant-n"));
    }

    private static Tenants CreateTenants(out Action<RedisChannel, RedisValue> handler)
    {
        Action<RedisChannel, RedisValue>? captured = null;
        var cacheClient = new Mock<ICacheClient>();
        cacheClient.Setup(c => c.SubscribeAsync(It.IsAny<string>(), It.IsAny<Action<RedisChannel, RedisValue>>()))
                   .Callback<string, Action<RedisChannel, RedisValue>>((_, h) => captured = h)
                   .Returns(Task.CompletedTask);
        cacheClient.Setup(c => c.UnsubscribeAsync(It.IsAny<string>())).Returns(Task.CompletedTask);

        var secret = new Mock<IBlocksSecret>();
        secret.SetupGet(s => s.DatabaseConnectionString).Returns(MongoConnectionString);
        secret.SetupGet(s => s.RootDatabaseName).Returns($"tenants-handler-{Guid.NewGuid():N}");

        var tenants = new Tenants(new Mock<ILogger<Tenants>>().Object, secret.Object, cacheClient.Object);
        Assert.NotNull(captured);
        handler = captured!;
        return tenants;
    }

    private static System.Collections.Concurrent.ConcurrentDictionary<string, Blocks.Genesis.Tenant> GetCache(Tenants tenants)
    {
        var field = typeof(Tenants).GetField("_tenantCache", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (System.Collections.Concurrent.ConcurrentDictionary<string, Blocks.Genesis.Tenant>)field!.GetValue(tenants)!;
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
}
