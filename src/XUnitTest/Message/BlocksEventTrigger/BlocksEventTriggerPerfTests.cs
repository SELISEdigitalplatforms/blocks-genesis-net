using Azure.Messaging.ServiceBus;
using Blocks.Genesis;
using MongoDB.Bson;
using System.Diagnostics;
using System.Globalization;
using Xunit.Abstractions;

namespace XUnitTest.Message.BlocksEventTrigger;

/// <summary>
/// H10: send-call latency with the hook off vs on, in one process, on a fake (no-op) transport, warm cache.
/// Opt-in: runs only with BLOCKS_PERF=1, because shared CI runners make timing assertions flaky.
/// </summary>
[Collection("BlocksAuthStaticState")]
[Trait("Category", "Perf")]
public sealed class BlocksEventTriggerPerfTests : IDisposable
{
    private const int Warmup = 1_000;
    private const int Iterations = 10_000;
    private const double BudgetMs = 1.0;

    private readonly ITestOutputHelper _output;

    public BlocksEventTriggerPerfTests(ITestOutputHelper output)
    {
        _output = output;
        TriggerKit.UseTenant("T-PERF");
    }

    public void Dispose() => TriggerKit.ClearTenant();

    private static bool Enabled => Environment.GetEnvironmentVariable("BLOCKS_PERF") == "1";

    private sealed class NoGrant : IDelegationGrantFactory
    {
        private static readonly Task<string?> None = Task.FromResult<string?>(null);

        public Task<string?> CreateForSendAsync(TimeSpan? ttl = null) => None;
    }

    [Fact]
    public async Task H10_Azure_HookAddsAtMostOneMillisecondP95OnACacheHit()
    {
        if (!Enabled)
        {
            return;
        }

        var catalog = TriggerKit.Catalog(("OrderPlaced", true), ("SmsQueued", false));
        var forwarder = TriggerKit.Forwarder(TriggerKit.Cache(TriggerKit.Store(catalog).Object));
        IMessageClient Client(IBlocksEventTriggerForwarder? f) => TriggerKit.AzureClient(f, new Dictionary<string, ServiceBusSender>
        {
            ["q"] = new RecordingSender("q") { Record = false },
            [BlocksEventTriggerConstants.QueueName] = new RecordingSender(BlocksEventTriggerConstants.QueueName) { Record = false }
        }, grantFactory: new NoGrant());

        await RunAsync("azure", Client(null), Client(forwarder));
    }

    [Fact]
    public async Task H10_Rabbit_HookAddsAtMostOneMillisecondP95OnACacheHit()
    {
        if (!Enabled)
        {
            return;
        }

        var catalog = TriggerKit.Catalog(("OrderPlaced", true), ("SmsQueued", false));
        var forwarder = TriggerKit.Forwarder(TriggerKit.Cache(TriggerKit.Store(catalog).Object));
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<RabbitMessageClient>.Instance;

        await RunAsync(
            "rabbit",
            TriggerKit.RabbitClient(null, TriggerKit.Channel().Object, logger, grantFactory: new NoGrant()),
            TriggerKit.RabbitClient(forwarder, TriggerKit.Channel().Object, logger, grantFactory: new NoGrant()));
    }

    private async Task RunAsync(string transport, IMessageClient off, IMessageClient on)
    {
        var disabled = new ConsumerMessage<SmsQueued> { ConsumerName = "q", Payload = new SmsQueued { Text = "hello" } };
        var enabled = new ConsumerMessage<OrderPlaced> { ConsumerName = "q", Payload = new OrderPlaced { OrderId = "o-1" } };

        // Hook off is measured for both message types, so each "on" run is compared with its own baseline.
        var offDisabled = await MeasureAsync(() => off.SendToConsumerAsync(disabled));
        var onDisabled = await MeasureAsync(() => on.SendToConsumerAsync(disabled));
        var offEnabled = await MeasureAsync(() => off.SendToConsumerAsync(enabled));
        var onEnabled = await MeasureAsync(() => on.SendToConsumerAsync(enabled));

        Report(transport, "hook off (not enabled event)", offDisabled, null);
        Report(transport, "hook on, hit, not enabled", onDisabled, offDisabled);
        Report(transport, "hook off (enabled event)", offEnabled, null);
        Report(transport, "hook on, hit, enabled + forward (no-op transport)", onEnabled, offEnabled);

        Assert.True(onDisabled.P95 - offDisabled.P95 <= BudgetMs, $"not-enabled Δp95 {onDisabled.P95 - offDisabled.P95:F4} ms");
        Assert.True(onEnabled.P95 - offEnabled.P95 <= BudgetMs, $"enabled Δp95 {onEnabled.P95 - offEnabled.P95:F4} ms");
    }

    private static async Task<Percentiles> MeasureAsync(Func<Task> send)
    {
        for (var i = 0; i < Warmup; i++)
        {
            await send();
        }

        var samples = new double[Iterations];
        for (var i = 0; i < Iterations; i++)
        {
            var start = Stopwatch.GetTimestamp();
            await send();
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        Array.Sort(samples);
        return new Percentiles(At(samples, 0.50), At(samples, 0.95), At(samples, 0.99));
    }

    private static double At(double[] sorted, double quantile) =>
        sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(quantile * sorted.Length) - 1)];

    private void Report(string transport, string scenario, Percentiles p, Percentiles? baseline)
    {
        var line = string.Format(
            CultureInfo.InvariantCulture,
            "{0} | {1} | p50={2:F4} ms p95={3:F4} ms p99={4:F4} ms{5}",
            transport, scenario, p.P50, p.P95, p.P99,
            baseline is null ? string.Empty : string.Format(CultureInfo.InvariantCulture, " | Δp50={0:F4} Δp95={1:F4} Δp99={2:F4}",
                p.P50 - baseline.P50, p.P95 - baseline.P95, p.P99 - baseline.P99));
        _output.WriteLine(line);

        var file = Environment.GetEnvironmentVariable("BLOCKS_PERF_OUT");
        if (!string.IsNullOrWhiteSpace(file))
        {
            File.AppendAllText(file, line + Environment.NewLine);
        }
    }

    private sealed record Percentiles(double P50, double P95, double P99);
}
