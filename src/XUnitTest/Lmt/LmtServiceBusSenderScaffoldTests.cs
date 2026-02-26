using SeliseBlocks.LMT.Client;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace XUnitTest.Lmt;

public class LmtServiceBusSenderScaffoldTests
{
    [Fact]
    public void Type_ShouldBeAccessible()
    {
        Assert.NotNull(typeof(LmtServiceBusSender));
    }

    [Fact]
    public async Task SendMethods_ShouldNotThrow_WhenSenderIsNotInitialized()
    {
        using var sender = new LmtServiceBusSender("svc", string.Empty, maxRetries: 1, maxFailedBatches: 2);

        await sender.SendLogsAsync([
            new LogData
            {
                Message = "m",
                Level = "Information",
                ServiceName = "svc",
                Timestamp = DateTime.UtcNow
            }
        ]);

        await sender.SendTracesAsync(new Dictionary<string, List<TraceData>>
        {
            ["tenant-1"] = [new TraceData { TraceId = "t", SpanId = "s" }]
        });
    }

    [Fact]
    public void Dispose_ShouldSetDisposedFlag()
    {
        var sender = new LmtServiceBusSender("svc", string.Empty);

        sender.Dispose();

        var field = typeof(LmtServiceBusSender).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        Assert.True((bool)field!.GetValue(sender)!);
    }

    [Fact]
    public async Task RetryFailedLogsAsync_ShouldKeepFutureBatchQueued()
    {
        var sender = CreateUninitializedSender(maxRetries: 3, maxFailedBatches: 10);
        var queue = GetLogQueue(sender);
        queue.Enqueue(new FailedLogBatch
        {
            Logs = [new LogData { Message = "m", Level = "Info", ServiceName = "svc", Timestamp = DateTime.UtcNow }],
            RetryCount = 0,
            NextRetryTime = DateTime.UtcNow.AddMinutes(10)
        });

        await InvokePrivateAsync(sender, "RetryFailedLogsAsync", DateTime.UtcNow);

        Assert.Single(queue);
    }

    [Fact]
    public async Task RetryFailedLogsAsync_ShouldDropBatch_WhenRetryCountExceeded()
    {
        var sender = CreateUninitializedSender(maxRetries: 1, maxFailedBatches: 10);
        var queue = GetLogQueue(sender);
        queue.Enqueue(new FailedLogBatch
        {
            Logs = [new LogData { Message = "m", Level = "Info", ServiceName = "svc", Timestamp = DateTime.UtcNow }],
            RetryCount = 1,
            NextRetryTime = DateTime.UtcNow.AddMinutes(-1)
        });

        await InvokePrivateAsync(sender, "RetryFailedLogsAsync", DateTime.UtcNow);

        Assert.Empty(queue);
    }

    [Fact]
    public async Task RetryFailedTracesAsync_ShouldKeepFutureBatchQueued()
    {
        var sender = CreateUninitializedSender(maxRetries: 3, maxFailedBatches: 10);
        var queue = GetTraceQueue(sender);
        queue.Enqueue(new FailedTraceBatch
        {
            TenantBatches = new Dictionary<string, List<TraceData>> { ["tenant-1"] = [new TraceData { TraceId = "t", SpanId = "s" }] },
            RetryCount = 0,
            NextRetryTime = DateTime.UtcNow.AddMinutes(10)
        });

        await InvokePrivateAsync(sender, "RetryFailedTracesAsync", DateTime.UtcNow);

        Assert.Single(queue);
    }

    [Fact]
    public async Task RetryFailedBatchesAsync_ShouldReturnImmediately_WhenSemaphoreUnavailable()
    {
        var sender = CreateUninitializedSender(maxRetries: 3, maxFailedBatches: 10);
        var semaphore = GetField<SemaphoreSlim>(sender, "_retrySemaphore");
        await semaphore.WaitAsync();

        try
        {
            await InvokePrivateAsync(sender, "RetryFailedBatchesAsync");
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static LmtServiceBusSender CreateUninitializedSender(int maxRetries, int maxFailedBatches)
    {
        var sender = (LmtServiceBusSender)RuntimeHelpers.GetUninitializedObject(typeof(LmtServiceBusSender));
        SetField(sender, "_serviceName", "svc");
        SetField(sender, "_maxRetries", maxRetries);
        SetField(sender, "_maxFailedBatches", maxFailedBatches);
        SetField(sender, "_failedLogBatches", new ConcurrentQueue<FailedLogBatch>());
        SetField(sender, "_failedTraceBatches", new ConcurrentQueue<FailedTraceBatch>());
        SetField(sender, "_retrySemaphore", new SemaphoreSlim(1, 1));
        SetField(sender, "_disposed", false);
        SetField(sender, "_retryTimer", new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite));
        return sender;
    }

    private static ConcurrentQueue<FailedLogBatch> GetLogQueue(LmtServiceBusSender sender)
        => GetField<ConcurrentQueue<FailedLogBatch>>(sender, "_failedLogBatches");

    private static ConcurrentQueue<FailedTraceBatch> GetTraceQueue(LmtServiceBusSender sender)
        => GetField<ConcurrentQueue<FailedTraceBatch>>(sender, "_failedTraceBatches");

    private static async Task InvokePrivateAsync(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = (Task)method!.Invoke(instance, args)!;
        await task;
    }

    private static T GetField<T>(object instance, string name)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (T)field!.GetValue(instance)!;
    }

    private static void SetField(object instance, string name, object? value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }
}
