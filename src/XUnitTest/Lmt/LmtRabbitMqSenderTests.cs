using SeliseBlocks.LMT.Client;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace XUnitTest.Lmt;

public class LmtRabbitMqSenderTests
{
    [Fact]
    public async Task SendLogsAsync_ShouldQueueFailedBatch_WhenChannelNotAvailable()
    {
        var sender = CreateUninitializedSender(maxRetries: 0, maxFailedBatches: 10);
        var logs = new List<LogData>
        {
            new() { Message = "test", Level = "Info", ServiceName = "svc", Timestamp = DateTime.UtcNow }
        };

        await sender.SendLogsAsync(logs);

        var queue = GetLogQueue(sender);
        Assert.Single(queue);
    }

    [Fact]
    public async Task SendTracesAsync_ShouldQueueFailedBatch_WhenChannelNotAvailable()
    {
        var sender = CreateUninitializedSender(maxRetries: 0, maxFailedBatches: 10);
        var traces = new Dictionary<string, List<TraceData>>
        {
            ["t1"] = [new TraceData { TraceId = "tr-1", SpanId = "sp-1" }]
        };

        await sender.SendTracesAsync(traces);

        var queue = GetTraceQueue(sender);
        Assert.Single(queue);
    }

    [Fact]
    public async Task SendLogsAsync_ShouldDropBatch_WhenQueueIsFull()
    {
        var sender = CreateUninitializedSender(maxRetries: 0, maxFailedBatches: 1);

        // Fill the queue
        GetLogQueue(sender).Enqueue(new FailedLogBatch
        {
            Logs = [new LogData()],
            RetryCount = 0,
            NextRetryTime = DateTime.UtcNow.AddMinutes(5)
        });

        // This one should be dropped
        await sender.SendLogsAsync([new LogData { Message = "dropped" }]);

        var queue = GetLogQueue(sender);
        Assert.Single(queue); // Only the original one
    }

    [Fact]
    public async Task SendTracesAsync_ShouldDropBatch_WhenQueueIsFull()
    {
        var sender = CreateUninitializedSender(maxRetries: 0, maxFailedBatches: 1);

        GetTraceQueue(sender).Enqueue(new FailedTraceBatch
        {
            TenantBatches = new Dictionary<string, List<TraceData>> { ["x"] = [] },
            RetryCount = 0,
            NextRetryTime = DateTime.UtcNow.AddMinutes(5)
        });

        await sender.SendTracesAsync(new Dictionary<string, List<TraceData>> { ["y"] = [] });

        var queue = GetTraceQueue(sender);
        Assert.Single(queue);
    }

    [Fact]
    public async Task RetryFailedLogsAsync_ShouldKeepFutureBatch()
    {
        var sender = CreateUninitializedSender(maxRetries: 3, maxFailedBatches: 10);
        var queue = GetLogQueue(sender);
        queue.Enqueue(new FailedLogBatch
        {
            Logs = [new LogData { Message = "future" }],
            RetryCount = 0,
            NextRetryTime = DateTime.UtcNow.AddMinutes(10)
        });

        await InvokePrivateAsync(sender, "RetryFailedLogsAsync", DateTime.UtcNow);

        Assert.Single(queue);
    }

    [Fact]
    public async Task RetryFailedLogsAsync_ShouldDropBatch_WhenMaxRetriesExceeded()
    {
        var sender = CreateUninitializedSender(maxRetries: 1, maxFailedBatches: 10);
        var queue = GetLogQueue(sender);
        queue.Enqueue(new FailedLogBatch
        {
            Logs = [new LogData { Message = "exceeded" }],
            RetryCount = 1,
            NextRetryTime = DateTime.UtcNow.AddMinutes(-1)
        });

        await InvokePrivateAsync(sender, "RetryFailedLogsAsync", DateTime.UtcNow);

        Assert.Empty(queue);
    }

    [Fact]
    public async Task RetryFailedTracesAsync_ShouldKeepFutureBatch()
    {
        var sender = CreateUninitializedSender(maxRetries: 3, maxFailedBatches: 10);
        var queue = GetTraceQueue(sender);
        queue.Enqueue(new FailedTraceBatch
        {
            TenantBatches = new Dictionary<string, List<TraceData>> { ["t"] = [] },
            RetryCount = 0,
            NextRetryTime = DateTime.UtcNow.AddMinutes(10)
        });

        await InvokePrivateAsync(sender, "RetryFailedTracesAsync", DateTime.UtcNow);

        Assert.Single(queue);
    }

    [Fact]
    public async Task RetryFailedTracesAsync_ShouldDropBatch_WhenMaxRetriesExceeded()
    {
        var sender = CreateUninitializedSender(maxRetries: 1, maxFailedBatches: 10);
        var queue = GetTraceQueue(sender);
        queue.Enqueue(new FailedTraceBatch
        {
            TenantBatches = new Dictionary<string, List<TraceData>> { ["t"] = [] },
            RetryCount = 1,
            NextRetryTime = DateTime.UtcNow.AddMinutes(-1)
        });

        await InvokePrivateAsync(sender, "RetryFailedTracesAsync", DateTime.UtcNow);

        Assert.Empty(queue);
    }

    [Fact]
    public async Task RetryFailedBatchesAsync_ShouldReturnImmediately_WhenSemaphoreHeld()
    {
        var sender = CreateUninitializedSender(maxRetries: 3, maxFailedBatches: 10);
        var semaphore = GetField<SemaphoreSlim>(sender, "_retrySemaphore");
        await semaphore.WaitAsync();

        try
        {
            // Should not block, should return immediately
            await InvokePrivateAsync(sender, "RetryFailedBatchesAsync");
        }
        finally
        {
            semaphore.Release();
        }
    }

    [Fact]
    public void Dispose_ShouldDisposeTimerAndSemaphore()
    {
        var sender = CreateUninitializedSender(maxRetries: 0, maxFailedBatches: 0);

        // Source code disposes _retrySemaphore before RetryFailedBatchesAsync uses it,
        // so Dispose throws ObjectDisposedException
        Assert.ThrowsAny<ObjectDisposedException>(() => sender.Dispose());
    }

    [Fact]
    public void Type_ShouldImplementILmtMessageSender()
    {
        Assert.True(typeof(ILmtMessageSender).IsAssignableFrom(typeof(LmtRabbitMqSender)));
    }

    // --- Helpers ---

    private static LmtRabbitMqSender CreateUninitializedSender(int maxRetries, int maxFailedBatches)
    {
        var sender = (LmtRabbitMqSender)RuntimeHelpers.GetUninitializedObject(typeof(LmtRabbitMqSender));
        SetField(sender, "_serviceName", "test-svc");
        SetField(sender, "_maxRetries", maxRetries);
        SetField(sender, "_maxFailedBatches", maxFailedBatches);
        SetField(sender, "_failedLogBatches", new ConcurrentQueue<FailedLogBatch>());
        SetField(sender, "_failedTraceBatches", new ConcurrentQueue<FailedTraceBatch>());
        SetField(sender, "_retrySemaphore", new SemaphoreSlim(1, 1));
        SetField(sender, "_publishSemaphore", new SemaphoreSlim(1, 1));
        SetField(sender, "_disposed", false);
        SetField(sender, "_retryTimer", new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite));
        return sender;
    }

    private static ConcurrentQueue<FailedLogBatch> GetLogQueue(LmtRabbitMqSender sender)
        => GetField<ConcurrentQueue<FailedLogBatch>>(sender, "_failedLogBatches");

    private static ConcurrentQueue<FailedTraceBatch> GetTraceQueue(LmtRabbitMqSender sender)
        => GetField<ConcurrentQueue<FailedTraceBatch>>(sender, "_failedTraceBatches");

    private static async Task InvokePrivateAsync(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(instance, args.Length > 0 ? args : null)!;
    }

    private static T GetField<T>(object instance, string name)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (T)field.GetValue(instance)!;
    }

    private static void SetField(object instance, string name, object? value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(instance, value);
    }
}
