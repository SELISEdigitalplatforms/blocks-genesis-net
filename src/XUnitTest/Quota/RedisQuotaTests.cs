using Blocks.Genesis;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;

namespace XUnitTest.Quota;

[Collection("BlocksAuthStaticState")]
public class RedisQuotaTests : IDisposable
{
    private const string TenantId = "tnt_dev";
    private const string Meter = "api.calls";

    private readonly Mock<ICacheClient> _cache = new();
    private readonly Mock<IDatabase> _db = new();
    private readonly Mock<IQuotaLimitStore> _store = new();
    private readonly Mock<ITenants> _tenants = new();
    private readonly QuotaFlushTracker _tracker = new();
    private readonly QuotaOptions _options = new();

    public RedisQuotaTests()
    {
        BlocksContext.IsTestMode = true;
        BlocksContext.ClearContext();
        BlocksContext.SetContext(Context(TenantId));
        _cache.Setup(c => c.CacheDatabase()).Returns(_db.Object);
        _tenants.Setup(t => t.GetTenantByID(It.IsAny<string>())).Returns(TenantRow(false));
    }

    public void Dispose()
    {
        BlocksContext.ClearContext();
        BlocksContext.IsTestMode = false;
        GC.SuppressFinalize(this);
    }

    private RedisQuota Subject() =>
        new(_cache.Object, _store.Object, _tenants.Object, _tracker, _options, NullLogger<RedisQuota>.Instance);

    private static Blocks.Genesis.Tenant TenantRow(bool root) => new()
    {
        IsRootTenant = root,
        DbConnectionString = "mongodb://localhost",
        JwtTokenParameters = new JwtTokenParameters { PrivateCertificatePassword = "x", IssueDate = DateTime.UnixEpoch }
    };

    private static BlocksContext Context(string tenantId) =>
        BlocksContext.Create(tenantId, ["admin"], "user-1", true, "/orders", "", DateTime.MinValue, "", [], "", "", "", "", tenantId, "");

    private void ScriptReturns(params RedisResult[] results)
    {
        var queue = new Queue<RedisResult>(results);
        _db.Setup(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), CommandFlags.None))
           .Returns(() => Task.FromResult(queue.Count > 0 ? queue.Dequeue() : Reply(0, -1, -1)));
    }

    private static RedisResult Reply(int outcome, long remaining, long limit) =>
        RedisResult.Create(new RedisValue[] { outcome, remaining, limit });

    [Fact]
    public async Task Allows_AndReportsWhatIsLeft()
    {
        ScriptReturns(Reply(0, 41, 100));

        var decision = await Subject().ConsumeAsync(Meter, 1, "req-1");

        Assert.Equal(QuotaOutcome.Allowed, decision.Outcome);
        Assert.True(decision.IsAllowed);
        Assert.Equal(41, decision.Remaining);
        Assert.Equal(100, decision.Limit);
    }

    [Fact]
    public async Task Denies_WhenTheCeilingWouldBeCrossed()
    {
        ScriptReturns(Reply(1, 0, 100));

        var decision = await Subject().ConsumeAsync(Meter, 1, "req-2");

        Assert.Equal(QuotaOutcome.Denied, decision.Outcome);
        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public async Task Duplicate_CountsNothingButStillProceeds()
    {
        ScriptReturns(Reply(2, -1, -1));

        var decision = await Subject().ConsumeAsync(Meter, 1, "build-77");

        Assert.Equal(QuotaOutcome.Duplicate, decision.Outcome);
        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public async Task ColdKey_SeedsFromTheStoreAndRetriesOnce()
    {
        ScriptReturns(Reply(3, -1, -1), RedisResult.Create(1), Reply(0, 99, 100));
        _store.Setup(s => s.GetAsync(TenantId, Meter, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new QuotaLimit(100, 0, 0, "2026-09-12"));

        var decision = await Subject().ConsumeAsync(Meter, 1, "req-3");

        Assert.Equal(QuotaOutcome.Allowed, decision.Outcome);
        _store.Verify(s => s.GetAsync(TenantId, Meter, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NoLimitRow_SkipsRatherThanBlocking()
    {
        ScriptReturns(Reply(3, -1, -1));
        _store.Setup(s => s.GetAsync(TenantId, Meter, It.IsAny<CancellationToken>()))
              .ReturnsAsync((QuotaLimit?)null);

        var decision = await Subject().ConsumeAsync(Meter, 1, "req-4");

        Assert.Equal(QuotaOutcome.Skipped, decision.Outcome);
        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public async Task RootTenant_IsNeverMetered()
    {
        _tenants.Setup(t => t.GetTenantByID(TenantId)).Returns(TenantRow(true));

        var decision = await Subject().ConsumeAsync(Meter, 1, "req-5");

        Assert.Equal(QuotaOutcome.Skipped, decision.Outcome);
        _db.Verify(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()), Times.Never);
    }

    [Fact]
    public async Task NoTenantContext_IsNeverMetered()
    {
        BlocksContext.ClearContext();

        var decision = await Subject().ConsumeAsync(Meter, 1, "req-6");

        Assert.Equal(QuotaOutcome.Skipped, decision.Outcome);
    }

    [Fact]
    public async Task UnreachableCounter_FailsOpenOnCheapMeters()
    {
        _db.Setup(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), CommandFlags.None))
           .ThrowsAsync(new RedisTimeoutException("down", CommandStatus.Unknown));

        var decision = await Subject().ConsumeAsync("api.calls", 1, "req-7");

        Assert.Equal(QuotaOutcome.FailedOpen, decision.Outcome);
        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public async Task UnreachableCounter_FailsClosedWhereEveryUnitIsSpend()
    {
        _db.Setup(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), CommandFlags.None))
           .ThrowsAsync(new RedisTimeoutException("down", CommandStatus.Unknown));

        var decision = await Subject().ConsumeAsync("ai.blocksCredits", 1, "llm-8");

        Assert.Equal(QuotaOutcome.FailedClosed, decision.Outcome);
        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public async Task SlowCounter_IsAbandonedAtTheTimeout()
    {
        // The call never completes. If the timeout did not fire, this test would hang rather than
        // fail — which is the point: a quota check must never become the latency floor.
        _options.Timeout = TimeSpan.FromMilliseconds(20);
        var neverAnswers = new TaskCompletionSource<RedisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _db.Setup(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), CommandFlags.None))
           .Returns(neverAnswers.Task);

        var decision = await Subject().ConsumeAsync(Meter, 1, "req-9");

        Assert.Equal(QuotaOutcome.FailedOpen, decision.Outcome);
        Assert.False(neverAnswers.Task.IsCompleted);
        neverAnswers.SetCanceled();
    }

    [Fact]
    public async Task KillSwitch_StopsEnforcementWithoutTouchingRedis()
    {
        _options.Disabled = true;

        var decision = await Subject().ConsumeAsync(Meter, 1, "req-10");

        Assert.Equal(QuotaOutcome.Skipped, decision.Outcome);
        _db.Verify(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()), Times.Never);
    }

    [Fact]
    public async Task KillSwitch_CanTargetASingleMeter()
    {
        _options.DisabledMeters.Add("ai.agents");
        ScriptReturns(Reply(0, 5, 10));

        Assert.Equal(QuotaOutcome.Skipped, (await Subject().ConsumeAsync("ai.agents", 1, "a-1")).Outcome);
        Assert.Equal(QuotaOutcome.Allowed, (await Subject().ConsumeAsync("api.calls", 1, "a-2")).Outcome);
    }

    [Fact]
    public async Task AnAllowedCall_IsQueuedForTheNextFlush()
    {
        ScriptReturns(Reply(0, 41, 100));

        await Subject().ConsumeAsync(Meter, 1, "req-11");

        var drained = _tracker.Drain();
        Assert.Single(drained);
        Assert.Equal([Meter], drained[TenantId]);
    }

    [Fact]
    public async Task ADeniedCall_IsNotQueued()
    {
        ScriptReturns(Reply(1, 0, 100));

        await Subject().ConsumeAsync(Meter, 1, "req-12");

        Assert.Equal(0, _tracker.PendingCount);
    }

    [Fact]
    public async Task AnIdempotencyKeyIsMandatory()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Subject().ConsumeAsync(Meter, 1, ""));
    }

    [Fact]
    public async Task GivingUnitsBack_IsTheSameCallWithTheSignFlipped()
    {
        RedisValue[]? sent = null;
        _db.Setup(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), CommandFlags.None))
           .Callback<string, RedisKey[], RedisValue[], CommandFlags>((_, _, v, _) => sent = v)
           .ReturnsAsync(Reply(0, 6, 10));

        var decision = await Subject().ConsumeAsync("ai.agents", -1, "agent-42");

        Assert.Equal(QuotaOutcome.Allowed, decision.Outcome);
        Assert.Equal(-1, (long)sent![0]);
    }
}
