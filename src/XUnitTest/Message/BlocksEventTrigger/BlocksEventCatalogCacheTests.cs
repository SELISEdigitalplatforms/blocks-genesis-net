using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Moq;
using StackExchange.Redis;

namespace XUnitTest.Message.BlocksEventTrigger;

[Collection("BlocksAuthStaticState")]
public sealed class BlocksEventCatalogCacheTests : IDisposable
{
    private static readonly Func<Times> Once = Times.Once;

    public BlocksEventCatalogCacheTests() => TriggerKit.UseTenant("T1");

    public void Dispose() => TriggerKit.ClearTenant();

    private static void VerifyReads(Mock<IKeyValueStore> store, Func<Times> times) =>
        store.Verify(s => s.GetAsync<BsonArray>(BlocksEventTriggerConstants.CatalogKey, true, It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task H6_ServesFromMemoryUntilLifetimeEnds()
    {
        var clock = new TriggerClock();
        var store = TriggerKit.Store(TriggerKit.Catalog(("OrderPlaced", true), ("SmsQueued", false)));
        var cache = TriggerKit.Cache(store.Object, clock: clock);

        var first = await cache.GetEnabledEventsAsync("T1");
        for (var i = 0; i < 1_000; i++)
        {
            var hit = cache.GetEnabledEventsAsync("T1");
            Assert.True(hit.IsCompletedSuccessfully, "a cache hit must complete synchronously");
        }

        clock.Advance(BlocksEventTriggerConstants.CacheLifetime - TimeSpan.FromSeconds(1));
        await cache.GetEnabledEventsAsync("T1");
        VerifyReads(store, Once);

        clock.Advance(TimeSpan.FromSeconds(1));
        await cache.GetEnabledEventsAsync("T1");
        VerifyReads(store, () => Times.Exactly(2));

        Assert.Contains("OrderPlaced", first);
        Assert.DoesNotContain("SmsQueued", first);
    }

    [Fact]
    public async Task H6_ZeroStoreOrRedisCallsOnCacheHits_StrictMocks()
    {
        var store = TriggerKit.Store(TriggerKit.Catalog(("OrderPlaced", true)), MockBehavior.Strict);
        var redis = TriggerKit.CacheClient(MockBehavior.Strict);
        var cache = TriggerKit.Cache(store.Object, redis.Object);

        await cache.GetEnabledEventsAsync("T1");

        for (var i = 0; i < 10_000; i++)
        {
            await cache.GetEnabledEventsAsync("T1");
        }

        VerifyReads(store, Once);
        redis.Verify(c => c.SubscribeAsync(BlocksEventTriggerConstants.InvalidationChannel, It.IsAny<Action<RedisChannel, RedisValue>>()), Times.Once);
        store.VerifyNoOtherCalls();
        redis.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task H7_InvalidationDropsOnlyThatTenant()
    {
        var store = TriggerKit.Store(TriggerKit.Catalog(("OrderPlaced", true)));
        var redis = TriggerKit.CacheClient(out var handler);
        var cache = TriggerKit.Cache(store.Object, redis.Object);

        TriggerKit.UseTenant("T1");
        await cache.GetEnabledEventsAsync("T1");
        TriggerKit.UseTenant("T2");
        await cache.GetEnabledEventsAsync("T2");
        VerifyReads(store, () => Times.Exactly(2));

        Assert.NotNull(handler());
        handler()!(RedisChannel.Literal(BlocksEventTriggerConstants.InvalidationChannel), "T1");

        TriggerKit.UseTenant("T2");
        await cache.GetEnabledEventsAsync("T2");
        VerifyReads(store, () => Times.Exactly(2));

        TriggerKit.UseTenant("T1");
        await cache.GetEnabledEventsAsync("T1");
        VerifyReads(store, () => Times.Exactly(3));
    }

    [Fact]
    public async Task H7_BlankInvalidationMessageIsIgnored()
    {
        var store = TriggerKit.Store(TriggerKit.Catalog(("OrderPlaced", true)));
        var redis = TriggerKit.CacheClient(out var handler);
        var cache = TriggerKit.Cache(store.Object, redis.Object);

        await cache.GetEnabledEventsAsync("T1");
        handler()!(RedisChannel.Literal(BlocksEventTriggerConstants.InvalidationChannel), RedisValue.Null);
        handler()!(RedisChannel.Literal(BlocksEventTriggerConstants.InvalidationChannel), " ");
        await cache.GetEnabledEventsAsync("T1");

        VerifyReads(store, Once);
    }

    [Fact]
    public async Task H7_InvalidationDuringReadIsNotOverwrittenByTheStaleResult()
    {
        var gate = new TaskCompletionSource<BsonArray?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var store = new Mock<IKeyValueStore>();
        store
            .Setup(s => s.GetAsync<BsonArray>(BlocksEventTriggerConstants.CatalogKey, true, It.IsAny<CancellationToken>()))
            .Returns(() => Interlocked.Increment(ref reads) == 1 ? gate.Task : Task.FromResult<BsonArray?>(TriggerKit.Catalog(("OrderPlaced", false))));
        var cache = TriggerKit.Cache(store.Object);

        var pending = cache.GetEnabledEventsAsync("T1").AsTask();
        cache.Invalidate("T1");
        gate.SetResult(TriggerKit.Catalog(("OrderPlaced", true)));

        Assert.Contains("OrderPlaced", await pending);
        Assert.DoesNotContain("OrderPlaced", await cache.GetEnabledEventsAsync("T1"));
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task H8_HundredConcurrentMissesShareOneRead()
    {
        var gate = new TaskCompletionSource<BsonArray?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Mock<IKeyValueStore>();
        store
            .Setup(s => s.GetAsync<BsonArray>(BlocksEventTriggerConstants.CatalogKey, true, It.IsAny<CancellationToken>()))
            .Returns(gate.Task);
        var cache = TriggerKit.Cache(store.Object);

        var lookups = Enumerable.Range(0, 100).Select(_ => cache.GetEnabledEventsAsync("T1").AsTask()).ToList();
        Assert.All(lookups, l => Assert.False(l.IsCompleted));

        gate.SetResult(TriggerKit.Catalog(("OrderPlaced", true)));
        var results = await Task.WhenAll(lookups);

        VerifyReads(store, Once);
        Assert.All(results, r => Assert.Contains("OrderPlaced", r));
    }

    [Fact]
    public async Task H8_HundredConcurrentMissesOnThreadPoolThreadsShareOneRead()
    {
        var gate = new TaskCompletionSource<BsonArray?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var store = new Mock<IKeyValueStore>();
        store
            .Setup(s => s.GetAsync<BsonArray>(BlocksEventTriggerConstants.CatalogKey, true, It.IsAny<CancellationToken>()))
            .Returns(() => { Interlocked.Increment(ref reads); return gate.Task; });
        var cache = TriggerKit.Cache(store.Object);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;

        // Each lookup resumes on its own thread-pool continuation once start is released, so they race.
        var lookups = Enumerable.Range(0, 100).Select(_ => Task.Run(async () =>
        {
            Interlocked.Increment(ref ready);
            await start.Task;
            return await cache.GetEnabledEventsAsync("T1");
        })).ToList();
        await TriggerKit.Eventually(() => Volatile.Read(ref ready) == 100);
        start.SetResult();
        await Task.Delay(200);
        gate.SetResult(TriggerKit.Catalog(("OrderPlaced", true)));
        var results = await Task.WhenAll(lookups);

        Assert.Equal(1, reads);
        Assert.All(results, r => Assert.Contains("OrderPlaced", r));
    }

    [Fact]
    public async Task M1_AReadThatNeverCompletesIsAbandonedAfterTheTimeoutAndCountsAsAFailure()
    {
        var clock = new TriggerClock();
        var hung = new TaskCompletionSource<BsonArray?>();
        var tokens = new List<CancellationToken>();
        var store = new Mock<IKeyValueStore>();
        store
            .Setup(s => s.GetAsync<BsonArray>(BlocksEventTriggerConstants.CatalogKey, true, It.IsAny<CancellationToken>()))
            .Returns<string, bool, CancellationToken>((_, _, token) => { tokens.Add(token); return hung.Task; });
        var logger = new RecordingLogger<BlocksEventCatalogCache>();
        var cache = TriggerKit.Cache(store.Object, clock: clock, logger: logger);

        var lookup = cache.GetEnabledEventsAsync("T1").AsTask();
        clock.Advance(BlocksEventCatalogCache.ReadTimeout - TimeSpan.FromMilliseconds(1));
        Assert.False(lookup.IsCompleted);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Same(lookup, await Task.WhenAny(lookup, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.Empty(await lookup);
        Assert.True(Assert.Single(tokens).IsCancellationRequested);
        var warning = Assert.Single(logger.At(LogLevel.Warning));
        Assert.Contains("T1", warning.Message);
        Assert.Contains(nameof(TimeoutException), warning.Message);

        // The failure is cached: later sends do no I/O and complete synchronously.
        for (var i = 0; i < 100; i++)
        {
            var hit = cache.GetEnabledEventsAsync("T1");
            Assert.True(hit.IsCompletedSuccessfully);
        }

        VerifyReads(store, Once);
        Assert.Equal(0, clock.PendingTimers);

        // The read that never completes may still fail later without surfacing anywhere.
        hung.SetException(new TimeoutException("late"));
    }

    [Fact]
    public async Task M1_FailureLifetimeCountsFromTheReadStartNotItsEnd()
    {
        var clock = new TriggerClock();
        var reads = 0;
        var store = new Mock<IKeyValueStore>();
        store
            .Setup(s => s.GetAsync<BsonArray>(BlocksEventTriggerConstants.CatalogKey, true, It.IsAny<CancellationToken>()))
            .Returns(() => ++reads == 1
                ? new TaskCompletionSource<BsonArray?>().Task
                : Task.FromResult<BsonArray?>(TriggerKit.Catalog(("OrderPlaced", true))));
        var cache = TriggerKit.Cache(store.Object, clock: clock);

        var lookup = cache.GetEnabledEventsAsync("T1").AsTask();
        clock.Advance(BlocksEventCatalogCache.ReadTimeout);
        Assert.Empty(await lookup.WaitAsync(TimeSpan.FromSeconds(5)));

        // The read started at t0 and ended at t0 + timeout; the entry expires at t0 + 30 s.
        clock.Advance(BlocksEventTriggerConstants.FailureCacheLifetime - BlocksEventCatalogCache.ReadTimeout - TimeSpan.FromSeconds(1));
        Assert.Empty(await cache.GetEnabledEventsAsync("T1"));
        Assert.Equal(1, reads);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Contains("OrderPlaced", await cache.GetEnabledEventsAsync("T1"));
        Assert.Equal(2, reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task M1_InvalidationForAnotherTenantDuringAReadKeepsThatReadsResult(bool readFails)
    {
        var gate = new TaskCompletionSource<BsonArray?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var store = new Mock<IKeyValueStore>();
        store
            .Setup(s => s.GetAsync<BsonArray>(BlocksEventTriggerConstants.CatalogKey, true, It.IsAny<CancellationToken>()))
            .Returns(() => { Interlocked.Increment(ref reads); return gate.Task; });
        var cache = TriggerKit.Cache(store.Object);

        var pending = cache.GetEnabledEventsAsync("T1").AsTask();
        cache.Invalidate("T2");
        if (readFails)
        {
            gate.SetException(new TimeoutException("mongo down"));
        }
        else
        {
            gate.SetResult(TriggerKit.Catalog(("OrderPlaced", true)));
        }

        await pending;
        var next = cache.GetEnabledEventsAsync("T1");

        Assert.True(next.IsCompletedSuccessfully, "T1's result (or its 30 s failure entry) must be cached");
        Assert.Equal(!readFails, next.Result.Contains("OrderPlaced"));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task M1_InvalidationBetweenTheCheckAndTheStoreDropsTheStaleResult()
    {
        var reads = 0;
        var store = new Mock<IKeyValueStore>();
        store
            .Setup(s => s.GetAsync<BsonArray>(BlocksEventTriggerConstants.CatalogKey, true, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult<BsonArray?>(Interlocked.Increment(ref reads) == 1
                ? TriggerKit.Catalog(("OrderPlaced", true))
                : TriggerKit.Catalog(("OrderPlaced", false))));
        var cache = TriggerKit.Cache(store.Object);
        var invalidated = false;
        cache.BeforeStore = tenant =>
        {
            if (!invalidated)
            {
                invalidated = true;
                cache.Invalidate(tenant);
            }
        };

        Assert.Contains("OrderPlaced", await cache.GetEnabledEventsAsync("T1"));

        Assert.DoesNotContain("OrderPlaced", await cache.GetEnabledEventsAsync("T1"));
        Assert.Equal(2, reads);
        Assert.Equal(1, cache.CachedTenantCount);
    }

    [Fact]
    public async Task M1_InvalidationForAnotherTenantBetweenTheCheckAndTheStoreKeepsTheResult()
    {
        var store = TriggerKit.Store(TriggerKit.Catalog(("OrderPlaced", true)));
        var cache = TriggerKit.Cache(store.Object);
        cache.BeforeStore = _ => cache.Invalidate("T2");

        await cache.GetEnabledEventsAsync("T1");
        await cache.GetEnabledEventsAsync("T1");

        VerifyReads(store, Once);
    }

    [Fact]
    public async Task M1_ClearingTheVersionMapStillDropsAReadThatAnInvalidationOverlapped()
    {
        var gate = new TaskCompletionSource<BsonArray?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var store = new Mock<IKeyValueStore>();
        store
            .Setup(s => s.GetAsync<BsonArray>(BlocksEventTriggerConstants.CatalogKey, true, It.IsAny<CancellationToken>()))
            .Returns(() => Interlocked.Increment(ref reads) == 1 ? gate.Task : Task.FromResult<BsonArray?>(null));
        var cache = TriggerKit.Cache(store.Object);

        var pending = cache.GetEnabledEventsAsync("T1").AsTask();
        cache.Invalidate("T1");
        // Enough other tenants to clear the version map: T1's version reads as "none" again afterwards.
        for (var i = 0; i < BlocksEventTriggerConstants.MaxCachedTenants; i++)
        {
            cache.Invalidate("other-" + i);
        }

        gate.SetResult(TriggerKit.Catalog(("OrderPlaced", true)));
        await pending;

        Assert.DoesNotContain("OrderPlaced", await cache.GetEnabledEventsAsync("T1"));
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task H9_EntriesWithUnknownFieldsDeserializeAndForward()
    {
        var entry = TriggerKit.Entry("OrderPlaced", true);
        entry["FutureField"] = "x";
        entry["Nested"] = new BsonDocument { ["a"] = 1 };
        var store = TriggerKit.Store(new BsonArray { entry });
        var logger = new RecordingLogger<BlocksEventCatalogCache>();
        var cache = TriggerKit.Cache(store.Object, logger: logger);

        Assert.Contains("OrderPlaced", await cache.GetEnabledEventsAsync("T1"));
        Assert.Empty(logger.At(LogLevel.Warning));

        var typed = BsonSerializer.Deserialize<BlocksEventCatalogEntry>(entry);
        Assert.Equal("OrderPlaced", typed.EventName);
        Assert.Equal(["iam"], typed.SourceServiceKeys);
        Assert.Equal(["blocks_iam_listener_user"], typed.ConsumerNames);
        Assert.True(typed.IsValidForWorkflow);
        Assert.False(typed.IsValidForFunction);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task H9_C12_ReadsARealCatalogDocumentThroughMongoKeyValueStore()
    {
        const string connection = "mongodb://127.0.0.1:27017";
        var client = new MongoClient(connection);
        var dbName = $"bet-cache-{Guid.NewGuid():N}";
        try
        {
            var db = client.GetDatabase(dbName);
            var extra = TriggerKit.Entry("OrderPlaced", true);
            extra["UnknownField"] = 42;
            var bad = TriggerKit.Entry("Broken", true);
            bad["IsValidForWorkflow"] = "true";
            var provider = new Mock<IDbContextProvider>();
            provider.Setup(p => p.GetDatabase()).Returns(db);
            var store = new MongoKeyValueStore(provider.Object, Mock.Of<IBlocksSecret>());
            await store.SetAsync(BlocksEventTriggerConstants.CatalogKey, new BsonArray { extra, bad, TriggerKit.Entry("SmsQueued", false) });
            var logger = new RecordingLogger<BlocksEventCatalogCache>();
            var cache = TriggerKit.Cache(store, logger: logger);

            var enabled = await cache.GetEnabledEventsAsync("T1");

            Assert.Equal(["OrderPlaced"], enabled.ToArray());
            Assert.Single(logger.At(LogLevel.Warning));
        }
        finally
        {
            await client.DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task C4_MissingDocumentIsCachedAsEmptyForTheFullLifetime()
    {
        var clock = new TriggerClock();
        var store = TriggerKit.Store((BsonArray?)null);
        var cache = TriggerKit.Cache(store.Object, clock: clock);

        for (var i = 0; i < 1_000; i++)
        {
            Assert.Empty(await cache.GetEnabledEventsAsync("T1"));
        }

        clock.Advance(BlocksEventTriggerConstants.CacheLifetime - TimeSpan.FromSeconds(1));
        await cache.GetEnabledEventsAsync("T1");
        VerifyReads(store, Once);

        clock.Advance(TimeSpan.FromSeconds(1));
        await cache.GetEnabledEventsAsync("T1");
        VerifyReads(store, () => Times.Exactly(2));
    }

    [Fact]
    public async Task C4_EmptyArrayIsTreatedAsNothingEnabled()
    {
        var cache = TriggerKit.Cache(TriggerKit.Store(new BsonArray()).Object);

        Assert.Empty(await cache.GetEnabledEventsAsync("T1"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task C5_ReadFailureCachesEmptyFor30sAndWarnsOncePer30s(bool throwSynchronously)
    {
        var clock = new TriggerClock();
        var store = new Mock<IKeyValueStore>();
        var setup = store.Setup(s => s.GetAsync<BsonArray>(BlocksEventTriggerConstants.CatalogKey, true, It.IsAny<CancellationToken>()));
        if (throwSynchronously)
        {
            setup.Throws(new TimeoutException("mongo down " + TriggerKit.PayloadMarker));
        }
        else
        {
            setup.Returns(Task.FromException<BsonArray?>(new TimeoutException("mongo down")));
        }

        var logger = new RecordingLogger<BlocksEventCatalogCache>();
        var cache = TriggerKit.Cache(store.Object, clock: clock, logger: logger);

        for (var i = 0; i < 100; i++)
        {
            Assert.Empty(await cache.GetEnabledEventsAsync("T1"));
        }

        clock.Advance(BlocksEventTriggerConstants.FailureCacheLifetime - TimeSpan.FromSeconds(1));
        await cache.GetEnabledEventsAsync("T1");
        VerifyReads(store, Once);
        var warning = Assert.Single(logger.At(LogLevel.Warning));
        Assert.Contains("T1", warning.Message);
        Assert.DoesNotContain(TriggerKit.PayloadMarker, logger.AllText());

        clock.Advance(TimeSpan.FromSeconds(1));
        await cache.GetEnabledEventsAsync("T1");
        VerifyReads(store, () => Times.Exactly(2));
        Assert.Equal(2, logger.At(LogLevel.Warning).Count);
    }

    [Fact]
    public async Task C9_CacheIsKeyedByTenant()
    {
        var store = TriggerKit.Store(tenant => tenant == "T1"
            ? TriggerKit.Catalog(("OrderPlaced", true))
            : TriggerKit.Catalog(("OrderPlaced", false)));
        var cache = TriggerKit.Cache(store.Object);

        TriggerKit.UseTenant("T1");
        Assert.Contains("OrderPlaced", await cache.GetEnabledEventsAsync("T1"));

        TriggerKit.UseTenant("T2");
        Assert.DoesNotContain("OrderPlaced", await cache.GetEnabledEventsAsync("T2"));

        TriggerKit.UseTenant("T1");
        Assert.Contains("OrderPlaced", await cache.GetEnabledEventsAsync("T1"));
        VerifyReads(store, () => Times.Exactly(2));
    }

    [Fact]
    public async Task C10_ClearsWhenFullBeforeAddingANewTenant()
    {
        var cache = TriggerKit.Cache(TriggerKit.Store((BsonArray?)null).Object);

        for (var i = 0; i < BlocksEventTriggerConstants.MaxCachedTenants; i++)
        {
            await cache.GetEnabledEventsAsync("tenant-" + i);
        }

        Assert.Equal(BlocksEventTriggerConstants.MaxCachedTenants, cache.CachedTenantCount);

        await cache.GetEnabledEventsAsync("tenant-new");

        Assert.Equal(1, cache.CachedTenantCount);
    }

    [Fact]
    public async Task C10_RefreshingAnExistingTenantWhenFullDoesNotClear()
    {
        var clock = new TriggerClock();
        var cache = TriggerKit.Cache(TriggerKit.Store((BsonArray?)null).Object, clock: clock);

        for (var i = 0; i < BlocksEventTriggerConstants.MaxCachedTenants; i++)
        {
            await cache.GetEnabledEventsAsync("tenant-" + i);
        }

        clock.Advance(BlocksEventTriggerConstants.CacheLifetime);
        await cache.GetEnabledEventsAsync("tenant-0");

        Assert.Equal(BlocksEventTriggerConstants.MaxCachedTenants, cache.CachedTenantCount);
    }

    [Fact]
    public async Task C11_SubscribeFailureNeverBlocksAndRetriesAtMostOncePerMinute()
    {
        var clock = new TriggerClock();
        var redis = new Mock<ICacheClient>();
        var attempts = 0;
        redis
            .Setup(c => c.SubscribeAsync(BlocksEventTriggerConstants.InvalidationChannel, It.IsAny<Action<RedisChannel, RedisValue>>()))
            .Returns(() => ++attempts < 3
                ? Task.FromException(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "redis down"))
                : Task.CompletedTask);
        var logger = new RecordingLogger<BlocksEventCatalogCache>();
        var store = TriggerKit.Store(TriggerKit.Catalog(("OrderPlaced", true)));
        var cache = TriggerKit.Cache(store.Object, redis.Object, clock, logger);

        Assert.Contains("OrderPlaced", await cache.GetEnabledEventsAsync("T1"));
        Assert.Equal(1, attempts);
        Assert.Single(logger.At(LogLevel.Warning));

        // Misses inside the minute do not retry.
        await cache.GetEnabledEventsAsync("T2");
        clock.Advance(TimeSpan.FromSeconds(59));
        await cache.GetEnabledEventsAsync("T3");
        Assert.Equal(1, attempts);

        // Hits never subscribe, even after the minute.
        clock.Advance(TimeSpan.FromSeconds(1));
        await cache.GetEnabledEventsAsync("T1");
        Assert.Equal(1, attempts);

        await cache.GetEnabledEventsAsync("T4");
        Assert.Equal(2, attempts);

        clock.Advance(TimeSpan.FromMinutes(1));
        await cache.GetEnabledEventsAsync("T5");
        Assert.Equal(3, attempts);

        // Subscribed: later misses do not subscribe again.
        clock.Advance(TimeSpan.FromMinutes(5));
        await cache.GetEnabledEventsAsync("T6");
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task C11_PendingSubscribeDoesNotBlockTheLookup()
    {
        var never = new TaskCompletionSource();
        var redis = new Mock<ICacheClient>();
        redis
            .Setup(c => c.SubscribeAsync(It.IsAny<string>(), It.IsAny<Action<RedisChannel, RedisValue>>()))
            .Returns(never.Task);
        var cache = TriggerKit.Cache(TriggerKit.Store(TriggerKit.Catalog(("OrderPlaced", true))).Object, redis.Object);

        var lookup = cache.GetEnabledEventsAsync("T1").AsTask();
        var winner = await Task.WhenAny(lookup, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(lookup, winner);
        Assert.Contains("OrderPlaced", await lookup);
        await cache.GetEnabledEventsAsync("T2");
        redis.Verify(c => c.SubscribeAsync(It.IsAny<string>(), It.IsAny<Action<RedisChannel, RedisValue>>()), Times.Once);
    }

    [Fact]
    public async Task C11_HungSubscribeTimesOutSoTheOncePerMinuteRetryStillHappens()
    {
        var clock = new TriggerClock();
        var attempts = new List<TaskCompletionSource>();
        var redis = new Mock<ICacheClient>();
        redis
            .Setup(c => c.SubscribeAsync(BlocksEventTriggerConstants.InvalidationChannel, It.IsAny<Action<RedisChannel, RedisValue>>()))
            .Returns(() =>
            {
                var attempt = new TaskCompletionSource();
                attempts.Add(attempt);
                return attempt.Task;
            });
        var logger = new RecordingLogger<BlocksEventCatalogCache>();
        var cache = TriggerKit.Cache(TriggerKit.Store(TriggerKit.Catalog(("OrderPlaced", true))).Object, redis.Object, clock, logger);

        Assert.Contains("OrderPlaced", await cache.GetEnabledEventsAsync("T1"));
        Assert.Single(attempts);

        clock.Advance(BlocksEventCatalogCache.SubscribeTimeout);
        await TriggerKit.Eventually(() => logger.At(LogLevel.Warning).Count == 1);
        var warning = Assert.Single(logger.At(LogLevel.Warning));
        Assert.Contains(nameof(TimeoutException), warning.Message);

        clock.Advance(BlocksEventCatalogCache.SubscribeRetryInterval - BlocksEventCatalogCache.SubscribeTimeout - TimeSpan.FromSeconds(1));
        await cache.GetEnabledEventsAsync("T2");
        Assert.Single(attempts);

        clock.Advance(TimeSpan.FromSeconds(1));
        await cache.GetEnabledEventsAsync("T3");
        Assert.Equal(2, attempts.Count);

        // The second attempt also hangs, then succeeds late: that counts as subscribed, so no third attempt.
        clock.Advance(BlocksEventCatalogCache.SubscribeTimeout);
        await TriggerKit.Eventually(() => logger.At(LogLevel.Warning).Count == 2);
        attempts[1].SetResult();
        clock.Advance(BlocksEventCatalogCache.SubscribeRetryInterval);
        await cache.GetEnabledEventsAsync("T4");
        Assert.Equal(2, attempts.Count);

        // A late failure of the first attempt surfaces nowhere.
        attempts[0].SetException(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "late"));
        Assert.Equal(2, logger.At(LogLevel.Warning).Count);
    }

    [Fact]
    public async Task C12_MalformedEntriesAreSkippedAndWarnedOncePerLifetime()
    {
        var clock = new TriggerClock();
        var missingName = TriggerKit.Entry("x", true);
        missingName.Remove("EventName");
        var blankName = TriggerKit.Entry("  ", true);
        var nonStringName = TriggerKit.Entry("x", true);
        nonStringName["EventName"] = 7;
        var stringFlag = TriggerKit.Entry("StringFlag", true);
        stringFlag["IsValidForWorkflow"] = "true";
        var intFlag = TriggerKit.Entry("IntFlag", true);
        intFlag["IsValidForWorkflow"] = 1;
        var badFunctionFlag = TriggerKit.Entry("BadFunctionFlag", true);
        badFunctionFlag["IsValidForFunction"] = "yes";
        var badArray = TriggerKit.Entry("BadArray", true);
        badArray["SourceServiceKeys"] = 5;
        var missingFlag = TriggerKit.Entry("MissingFlag", true);
        missingFlag.Remove("IsValidForWorkflow");

        var catalog = new BsonArray
        {
            TriggerKit.Entry("OrderPlaced", true),
            missingName, blankName, nonStringName, stringFlag, intFlag, badFunctionFlag, badArray,
            "not-a-document",
            missingFlag,
            TriggerKit.Entry("SmsQueued", false),
            TriggerKit.Entry("OrderPlaced", true)
        };
        var store = TriggerKit.Store(catalog);
        var logger = new RecordingLogger<BlocksEventCatalogCache>();
        var cache = TriggerKit.Cache(store.Object, clock: clock, logger: logger);

        var enabled = await cache.GetEnabledEventsAsync("T1");
        for (var i = 0; i < 50; i++)
        {
            await cache.GetEnabledEventsAsync("T1");
        }

        Assert.Equal(["OrderPlaced"], enabled.ToArray());
        var warning = Assert.Single(logger.At(LogLevel.Warning));
        Assert.Contains("T1", warning.Message);
        Assert.Contains("8", warning.Message);

        clock.Advance(BlocksEventTriggerConstants.CacheLifetime);
        await cache.GetEnabledEventsAsync("T1");
        Assert.Equal(2, logger.At(LogLevel.Warning).Count);
    }

    [Fact]
    public async Task C12_NonArrayValueFallsBackToTheFailurePath()
    {
        var store = new Mock<IKeyValueStore>();
        store
            .Setup(s => s.GetAsync<BsonArray>(BlocksEventTriggerConstants.CatalogKey, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FormatException("not an array"));
        var cache = TriggerKit.Cache(store.Object);

        Assert.Empty(await cache.GetEnabledEventsAsync("T1"));
    }
}
