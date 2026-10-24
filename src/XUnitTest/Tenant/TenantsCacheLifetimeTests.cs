using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Moq;
using StackExchange.Redis;
using System.Reflection;
using System.Text.Json;

namespace XUnitTest.Tenant;

public class TenantsCacheLifetimeTests
{
    private const string TenantId = "tenant-ttl";
    private const string RootTenantId = "root-tenant";
    private const string AppDomain = "app.local";
    private const string FirstDb = "db-v1";
    private const string SecondDb = "db-v2";
    private const string DbDown = "database unavailable";
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GetTenantByID_RefusesDisabledTenant_LikeAnUnknownOne()
    {
        // The database read filters out disabled tenants, so a disabled one reads as missing.
        var harness = new Harness();
        harness.Db.Next = () => null;

        Assert.Null(harness.Sut.GetTenantByID(TenantId));
        Assert.Null(harness.Sut.GetTenantByID("tenant-unknown"));
        Assert.Empty(harness.Cache);
        Assert.Equal((null, null), harness.Sut.GetTenantDatabaseConnectionString(TenantId));
        Assert.Null(harness.Sut.GetTenantTokenValidationParameter(TenantId));
    }

    [Fact]
    public void DisableEvent_EvictsCachedTenant_AndLookupIsRefused()
    {
        var harness = new Harness();
        harness.Db.Next = () => MakeTenant(TenantId);
        Assert.NotNull(harness.Sut.GetTenantByID(TenantId));
        Assert.True(harness.Cache.ContainsKey(TenantId));

        var disabled = MakeTenant(TenantId);
        disabled.IsDisabled = true;
        harness.Db.Next = () => null;
        harness.Publish(Tenants.TenantCacheUpdateActionUpsert, disabled);

        Assert.False(harness.Cache.ContainsKey(TenantId));
        Assert.Null(harness.Sut.GetTenantByID(TenantId));
        Assert.Empty(harness.Sut.GetTenantDatabaseConnectionStrings());
    }

    [Fact]
    public void ReEnabledTenant_IsServedAgain_OnNextLookup()
    {
        var harness = new Harness();
        var disabled = MakeTenant(TenantId);
        disabled.IsDisabled = true;
        harness.Publish(Tenants.TenantCacheUpdateActionUpsert, disabled);
        harness.Db.Next = () => null;
        Assert.Null(harness.Sut.GetTenantByID(TenantId));

        harness.Db.Next = () => MakeTenant(TenantId);

        var served = harness.Sut.GetTenantByID(TenantId);

        Assert.NotNull(served);
        Assert.True(harness.Cache.ContainsKey(TenantId));
    }

    [Fact]
    public void ReEnableEvent_CachesTenantAgain_WithoutDatabaseRead()
    {
        var harness = new Harness();

        harness.Publish(Tenants.TenantCacheUpdateActionUpsert, MakeTenant(TenantId));

        Assert.NotNull(harness.Sut.GetTenantByID(TenantId));
        Assert.Equal(0, harness.Db.Calls);
    }

    [Fact]
    public void ExpiredEntry_IsReloadedFromDatabase()
    {
        var harness = new Harness();
        harness.Db.Next = () => MakeTenant(TenantId, dbName: FirstDb);
        Assert.Equal(FirstDb, harness.Sut.GetTenantByID(TenantId)!.DBName);

        harness.Db.Next = () => MakeTenant(TenantId, dbName: SecondDb);
        harness.Time.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(FirstDb, harness.Sut.GetTenantByID(TenantId)!.DBName);
        Assert.Equal(1, harness.Db.Calls);

        harness.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(SecondDb, harness.Sut.GetTenantByID(TenantId)!.DBName);
        Assert.Equal(2, harness.Db.Calls);
        Assert.Equal(harness.Time.GetUtcNow() + Tenants.DefaultCacheLifetime, harness.Cache.ExpiresAt(TenantId));
    }

    [Fact]
    public void ExpiredEntry_DisabledWithoutEvent_IsEvictedAndRefused()
    {
        var harness = new Harness();
        harness.Db.Next = () => MakeTenant(TenantId);
        Assert.NotNull(harness.Sut.GetTenantByID(TenantId));

        // The disable event was lost; the database now answers "not found" for the tenant.
        harness.Db.Next = () => null;
        harness.Time.Advance(Tenants.DefaultCacheLifetime);

        Assert.Null(harness.Sut.GetTenantByID(TenantId));
        Assert.False(harness.Cache.ContainsKey(TenantId));
    }

    [Fact]
    public void ConcurrentLookupsAfterExpiry_ReadDatabaseOnce()
    {
        var harness = new Harness();
        harness.Db.Next = () => MakeTenant(TenantId, dbName: FirstDb);
        Assert.NotNull(harness.Sut.GetTenantByID(TenantId));
        harness.Time.Advance(Tenants.DefaultCacheLifetime + TimeSpan.FromSeconds(1));

        using var release = new ManualResetEventSlim(false);
        harness.Db.Next = () =>
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return MakeTenant(TenantId, dbName: SecondDb);
        };

        const int callers = 16;
        var results = new Blocks.Genesis.Tenant?[callers];
        using var ready = new CountdownEvent(callers);
        var threads = Enumerable.Range(0, callers).Select(i => new Thread(() =>
        {
            ready.Signal();
            results[i] = harness.Sut.GetTenantByID(TenantId);
        })).ToList();

        threads.ForEach(t => t.Start());
        Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(SpinWait.SpinUntil(() => harness.Db.Calls == 2, TimeSpan.FromSeconds(10)));
        release.Set();
        threads.ForEach(t => Assert.True(t.Join(TimeSpan.FromSeconds(10))));

        Assert.Equal(2, harness.Db.Calls);
        Assert.All(results, r => Assert.Equal(SecondDb, r!.DBName));
    }

    [Fact]
    public void ReloadFailure_ServesLastGoodEntry_LogsWarning_AndRetriesAfterDelay()
    {
        var harness = new Harness();
        harness.Db.Next = () => MakeTenant(TenantId, dbName: FirstDb);
        Assert.NotNull(harness.Sut.GetTenantByID(TenantId));

        harness.Db.Next = () => throw new TimeoutException(DbDown);
        harness.Time.Advance(Tenants.DefaultCacheLifetime);

        Assert.Equal(FirstDb, harness.Sut.GetTenantByID(TenantId)!.DBName);
        Assert.Equal(2, harness.Db.Calls);
        harness.VerifyLogged(LogLevel.Warning, Times.Once());

        // Within the retry delay the last good copy is served without another read.
        Assert.Equal(FirstDb, harness.Sut.GetTenantByID(TenantId)!.DBName);
        Assert.Equal(2, harness.Db.Calls);

        harness.Db.Next = () => MakeTenant(TenantId, dbName: SecondDb);
        harness.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(SecondDb, harness.Sut.GetTenantByID(TenantId)!.DBName);
        Assert.Equal(3, harness.Db.Calls);
    }

    [Fact]
    public void ReloadFailure_DoesNotServeDisabledEntry()
    {
        var harness = new Harness();
        var disabled = MakeTenant(TenantId);
        disabled.IsDisabled = true;
        harness.Cache.Set(TenantId, disabled, Start.AddMinutes(10));
        harness.Db.Next = () => throw new TimeoutException(DbDown);

        Assert.Null(harness.Sut.GetTenantByID(TenantId));
        Assert.False(harness.Cache.ContainsKey(TenantId));
        harness.VerifyLogged(LogLevel.Error, Times.Once());
    }

    [Fact]
    public void LoadFailure_WithoutCachedEntry_IsRefused()
    {
        var harness = new Harness();
        harness.Db.Next = () => throw new TimeoutException(DbDown);

        Assert.Null(harness.Sut.GetTenantByID(TenantId));
        Assert.Empty(harness.Cache);
    }

    [Fact]
    public void RootTenant_IsServedAndReloaded_LikeAnyTenant()
    {
        var harness = new Harness();
        harness.Db.Next = () =>
        {
            var root = MakeTenant(RootTenantId);
            root.IsRootTenant = true;
            return root;
        };

        Assert.True(harness.Sut.GetTenantByID(RootTenantId)!.IsRootTenant);

        harness.Time.Advance(Tenants.DefaultCacheLifetime);
        var reloaded = harness.Sut.GetTenantByID(RootTenantId);

        Assert.True(reloaded!.IsRootTenant);
        Assert.Equal(2, harness.Db.Calls);
    }

    [Fact]
    public void ExpiredEntry_FoundByDomain_IsReloadedBeforeItIsServed()
    {
        var harness = new Harness();
        harness.Db.Next = () => MakeTenant(TenantId);
        Assert.NotNull(harness.Sut.GetTenantByApplicationDomain(AppDomain));
        Assert.Equal(1, harness.Db.Calls);

        Assert.NotNull(harness.Sut.GetTenantByApplicationDomain(AppDomain));
        Assert.Equal(1, harness.Db.Calls);

        // Disabled in the database: the reload by ID and the domain query both find nothing.
        harness.Db.Next = () => null;
        harness.Time.Advance(Tenants.DefaultCacheLifetime);

        Assert.Null(harness.Sut.GetTenantByApplicationDomain(AppDomain));
        Assert.Equal(3, harness.Db.Calls);
        Assert.Empty(harness.Cache);
    }

    [Theory]
    [InlineData("60", 60)]
    [InlineData(" 15 ", 15)]
    [InlineData(null, 300)]
    [InlineData("0", 300)]
    [InlineData("-5", 300)]
    [InlineData("five", 300)]
    public void CacheLifetime_ComesFromEnvironment_WithDefaultFallback(string? configured, int expectedSeconds)
    {
        var harness = new Harness(configured);
        harness.Db.Next = () => MakeTenant(TenantId);

        harness.Sut.GetTenantByID(TenantId);

        Assert.Equal(Start.AddSeconds(expectedSeconds), harness.Cache.ExpiresAt(TenantId));
    }

    private static Blocks.Genesis.Tenant MakeTenant(string tenantId, string dbName = "db")
    {
        return new Blocks.Genesis.Tenant
        {
            TenantId = tenantId,
            DBName = dbName,
            DbConnectionString = "conn",
            CreatedDate = Start.UtcDateTime.AddDays(-30),
            Applications = [new Applications { Domain = "https://app.local" }],
            JwtTokenParameters = new JwtTokenParameters
            {
                Issuer = "issuer",
                Subject = "subject",
                Audiences = ["aud"],
                PublicCertificatePath = "path",
                PublicCertificatePassword = string.Empty,
                PrivateCertificatePassword = string.Empty,
                IssueDate = Start.UtcDateTime
            }
        };
    }

    private sealed class Harness
    {
        private readonly Mock<ILogger<Tenants>> _logger = new();
        private Action<RedisChannel, RedisValue>? _handler;

        public Harness(string? configuredLifetime = null)
        {
            var cacheClient = new Mock<ICacheClient>();
            cacheClient
                .Setup(c => c.SubscribeAsync(It.IsAny<string>(), It.IsAny<Action<RedisChannel, RedisValue>>()))
                .Callback<string, Action<RedisChannel, RedisValue>>((_, h) => _handler = h)
                .Returns(Task.CompletedTask);

            var secret = new Mock<IBlocksSecret>();
            secret.SetupGet(s => s.DatabaseConnectionString).Returns("mongodb://127.0.0.1:1");
            secret.SetupGet(s => s.RootDatabaseName).Returns("tenants-ttl");

            Sut = new Tenants(
                _logger.Object,
                secret.Object,
                cacheClient.Object,
                Time,
                name => name == Tenants.CacheLifetimeEnvironmentVariable ? configuredLifetime : null);

            typeof(Tenants)
                .GetField("_database", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(Sut, Db.Database);

            Cache = TenantCacheView.Of(Sut);
        }

        public ManualTime Time { get; } = new(Start);
        public TenantDbStub Db { get; } = new();
        public Tenants Sut { get; }
        public TenantCacheView Cache { get; }

        public void Publish(string action, Blocks.Genesis.Tenant tenant)
        {
            Assert.NotNull(_handler);
            var payload = JsonSerializer.Serialize(new TenantCacheUpdateMessage { Action = action, Tenant = tenant });
            _handler(new RedisChannel("tenant::updates", RedisChannel.PatternMode.Literal), payload);
        }

        public void VerifyLogged(LogLevel level, Times times)
        {
            _logger.Verify(
                l => l.Log(
                    level,
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception?>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                times);
        }
    }

    private sealed class TenantDbStub
    {
        private int _calls;

        public TenantDbStub()
        {
            var collection = new Mock<IMongoCollection<Blocks.Genesis.Tenant>>();
            collection
                .Setup(c => c.FindSync(
                    It.IsAny<FilterDefinition<Blocks.Genesis.Tenant>>(),
                    It.IsAny<FindOptions<Blocks.Genesis.Tenant, Blocks.Genesis.Tenant>>(),
                    It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    Interlocked.Increment(ref _calls);
                    return CreateCursor(Next());
                });

            var database = new Mock<IMongoDatabase>();
            database
                .Setup(d => d.GetCollection<Blocks.Genesis.Tenant>(It.IsAny<string>(), It.IsAny<MongoCollectionSettings>()))
                .Returns(collection.Object);
            Database = database.Object;
        }

        public IMongoDatabase Database { get; }
        public Func<Blocks.Genesis.Tenant?> Next { get; set; } = () => null;
        public int Calls => Volatile.Read(ref _calls);

        private static IAsyncCursor<Blocks.Genesis.Tenant> CreateCursor(Blocks.Genesis.Tenant? tenant)
        {
            var items = tenant is null ? new List<Blocks.Genesis.Tenant>() : [tenant];
            var cursor = new Mock<IAsyncCursor<Blocks.Genesis.Tenant>>();
            cursor.SetupSequence(c => c.MoveNext(It.IsAny<CancellationToken>()))
                .Returns(true)
                .Returns(false);
            cursor.SetupGet(c => c.Current).Returns(items);
            return cursor.Object;
        }
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
