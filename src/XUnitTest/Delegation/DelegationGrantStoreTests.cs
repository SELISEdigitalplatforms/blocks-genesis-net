using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using System.Text.Json;

namespace XUnitTest.Delegation;

public class DelegationGrantStoreTests
{
    private static BlocksContext AuthenticatedContext(string tenantId = "tenant-1", string userId = "user-1", string orgId = "org-1")
        => BlocksContext.Create(
            tenantId: tenantId,
            roles: ["admin"],
            userId: userId,
            isAuthenticated: true,
            requestUri: "https://unit.test",
            organizationId: orgId,
            expireOn: DateTime.UtcNow.AddHours(1),
            email: "user@unit.test",
            permissions: ["p1"],
            userName: "user",
            phoneNumber: "+10000000000",
            displayName: "User",
            oauthToken: "token",
            originalTenantId: tenantId);

    private static (DelegationGrantStore Store, Mock<IDatabase> Database, Mock<ICacheClient> Cache) CreateStore()
    {
        var database = new Mock<IDatabase>();
        var cache = new Mock<ICacheClient>();
        cache.Setup(c => c.CacheDatabase()).Returns(database.Object);

        var store = new DelegationGrantStore(cache.Object, new Mock<ILogger<DelegationGrantStore>>().Object);
        return (store, database, cache);
    }

    [Fact]
    public async Task CreateAsync_ShouldWriteRecordWithTwoDayTtl_WhenNoOverrideGiven()
    {
        var (store, database, _) = CreateStore();

        RedisKey capturedKey = default;
        RedisValue capturedValue = default;
        TimeSpan? capturedExpiry = null;

        database
            .Setup(db => db.StringSetAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .Callback((RedisKey key, RedisValue value, TimeSpan? expiry, bool _, When __, CommandFlags ___) =>
            {
                capturedKey = key;
                capturedValue = value;
                capturedExpiry = expiry;
            })
            .ReturnsAsync(true);

        var id = await store.CreateAsync(AuthenticatedContext(), tokenVersion: "3", securityStamp: "stamp-9");

        Assert.Equal(DelegationConstants.GrantKeyPrefix + id, capturedKey.ToString());
        Assert.Equal(DelegationConstants.DefaultGrantTtl, capturedExpiry);

        var record = JsonSerializer.Deserialize<DelegationGrantRecord>(capturedValue.ToString());
        Assert.NotNull(record);
        Assert.Equal("tenant-1", record!.TenantId);
        Assert.Equal("user-1", record.UserId);
        Assert.Equal("org-1", record.OrganizationId);
        Assert.Equal("3", record.TokenVersion);
        Assert.Equal("stamp-9", record.SecurityStamp);
    }

    [Fact]
    public async Task CreateAsync_ShouldHonourTtlOverride()
    {
        var (store, database, _) = CreateStore();
        TimeSpan? capturedExpiry = null;

        database
            .Setup(db => db.StringSetAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .Callback((RedisKey _, RedisValue __, TimeSpan? expiry, bool ___, When ____, CommandFlags _____) => capturedExpiry = expiry)
            .ReturnsAsync(true);

        await store.CreateAsync(AuthenticatedContext(), "1", "stamp", TimeSpan.FromHours(6));

        Assert.Equal(TimeSpan.FromHours(6), capturedExpiry);
    }

    [Fact]
    public async Task CreateAsync_ShouldFallBackToDefaultTtl_WhenOverrideIsNotPositive()
    {
        var (store, database, _) = CreateStore();
        TimeSpan? capturedExpiry = null;

        database
            .Setup(db => db.StringSetAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(),
                It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .Callback((RedisKey _, RedisValue __, TimeSpan? expiry, bool ___, When ____, CommandFlags _____) => capturedExpiry = expiry)
            .ReturnsAsync(true);

        await store.CreateAsync(AuthenticatedContext(), "1", "stamp", TimeSpan.Zero);

        Assert.Equal(DelegationConstants.DefaultGrantTtl, capturedExpiry);
    }

    [Fact]
    public async Task CreateAsync_ShouldReject_WhenContextHasNoAuthenticatedUser()
    {
        var (store, _, _) = CreateStore();

        var contextWithoutUser = BlocksContext.Create(
            tenantId: "tenant-1", roles: null, userId: null, isAuthenticated: false,
            requestUri: null, organizationId: null, expireOn: DateTime.UtcNow,
            email: null, permissions: null, userName: null, phoneNumber: null,
            displayName: null, oauthToken: null, originalTenantId: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(contextWithoutUser, "1", "stamp"));
    }

    [Fact]
    public void NewGrantId_ShouldBePrefixedHex_AndUnique()
    {
        var ids = Enumerable.Range(0, 200).Select(_ => DelegationGrantStore.NewGrantId()).ToList();

        Assert.All(ids, id =>
        {
            Assert.StartsWith(DelegationConstants.GrantIdPrefix, id, StringComparison.Ordinal);
            Assert.Equal(DelegationConstants.GrantIdPrefix.Length + 64, id.Length);
            Assert.True(DelegationGrantStore.IsWellFormed(id));
        });

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("dg_", false)]
    [InlineData("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff", false)]
    [InlineData("dg_00112233445566778899AABBCCDDEEFF00112233445566778899aabbccddeeff", false)]
    [InlineData("dg_00112233445566778899aabbccddeeff00112233445566778899aabbccddeef", false)]
    [InlineData("dg_00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff", true)]
    public void IsWellFormed_ShouldAcceptOnlyLowercaseHexOfExactLength(string? candidate, bool expected)
    {
        Assert.Equal(expected, DelegationGrantStore.IsWellFormed(candidate));
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_ForMalformedIdWithoutTouchingRedis()
    {
        var (store, _, cache) = CreateStore();

        Assert.Null(await store.GetAsync("not-a-grant"));

        cache.Verify(c => c.GetStringValueAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenStoredJsonIsUnreadable()
    {
        var (store, _, cache) = CreateStore();
        var id = DelegationTestDoubles.SampleGrantId();

        cache.Setup(c => c.GetStringValueAsync(DelegationConstants.GrantKeyPrefix + id)).ReturnsAsync("{not-json");

        Assert.Null(await store.GetAsync(id));
    }

    [Fact]
    public async Task GetAsync_ShouldRoundTripTheRecord()
    {
        var (store, _, cache) = CreateStore();
        var id = DelegationTestDoubles.SampleGrantId();

        var stored = JsonSerializer.Serialize(new DelegationGrantRecord
        {
            TenantId = "t", UserId = "u", OrganizationId = "o", TokenVersion = "7", SecurityStamp = "s"
        });
        cache.Setup(c => c.GetStringValueAsync(DelegationConstants.GrantKeyPrefix + id)).ReturnsAsync(stored);

        var record = await store.GetAsync(id);

        Assert.NotNull(record);
        Assert.Equal("u", record!.UserId);
        Assert.Equal("7", record.TokenVersion);
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveTheGrantKey()
    {
        var (store, _, cache) = CreateStore();
        var id = DelegationTestDoubles.SampleGrantId();
        cache.Setup(c => c.RemoveKeyAsync(It.IsAny<string>())).ReturnsAsync(true);

        await store.DeleteAsync(id);

        cache.Verify(c => c.RemoveKeyAsync(DelegationConstants.GrantKeyPrefix + id), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_ShouldSwallowRedisFailures_SoTheTtlRemainsTheBackstop()
    {
        var (store, _, cache) = CreateStore();
        cache.Setup(c => c.RemoveKeyAsync(It.IsAny<string>())).ThrowsAsync(new TimeoutException("redis down"));

        var exception = await Record.ExceptionAsync(() => store.DeleteAsync(DelegationTestDoubles.SampleGrantId()));

        Assert.Null(exception);
    }

    [Fact]
    public void RecordSerialization_ShouldUsePascalCaseFieldNames()
    {
        var json = JsonSerializer.Serialize(new DelegationGrantRecord
        {
            TenantId = "t", UserId = "u", OrganizationId = "o", TokenVersion = "1", SecurityStamp = "s"
        });

        // The Python SDK and IAM read these exact names.
        Assert.Contains("\"TenantId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"UserId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"OrganizationId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"TokenVersion\"", json, StringComparison.Ordinal);
        Assert.Contains("\"SecurityStamp\"", json, StringComparison.Ordinal);
    }
}
