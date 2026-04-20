using SeliseBlocks.LMT.Client;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Moq;
using RabbitMQ.Client;

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
    public async Task SendLogsAsync_ShouldRetryBeforeQueueing_WhenRetriesConfigured()
    {
        var sender = CreateUninitializedSender(maxRetries: 1, maxFailedBatches: 10);

        await sender.SendLogsAsync([new LogData { Message = "retry-log" }]);

        var queue = GetLogQueue(sender);
        Assert.Single(queue);
        Assert.Equal(1, queue.First().RetryCount);
    }

    [Fact]
    public async Task SendTracesAsync_ShouldRetryBeforeQueueing_WhenRetriesConfigured()
    {
        var sender = CreateUninitializedSender(maxRetries: 1, maxFailedBatches: 10);

        await sender.SendTracesAsync(new Dictionary<string, List<TraceData>>
        {
            ["tenant-a"] = [new TraceData { TraceId = "t", SpanId = "s" }]
        });

        var queue = GetTraceQueue(sender);
        Assert.Single(queue);
        Assert.Equal(1, queue.First().RetryCount);
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
    public async Task RetryFailedLogsAsync_ShouldSendDueBatch_WhenRetryAllowed()
    {
        var sender = CreateUninitializedSender(maxRetries: 3, maxFailedBatches: 10);

        var connection = new Mock<IConnection>();
        connection.SetupGet(c => c.IsOpen).Returns(true);

        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<BasicProperties>(),
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        SetField(sender, "_connection", connection.Object);
        SetField(sender, "_channel", channel.Object);

        var queue = GetLogQueue(sender);
        queue.Enqueue(new FailedLogBatch
        {
            Logs = [new LogData { Message = "due", ServiceName = "svc", Level = "Info", Timestamp = DateTime.UtcNow }],
            RetryCount = 0,
            NextRetryTime = DateTime.UtcNow.AddSeconds(-1)
        });

        await InvokePrivateAsync(sender, "RetryFailedLogsAsync", DateTime.UtcNow);

        Assert.Empty(queue);
        channel.Verify(c => c.BasicPublishAsync(
            It.IsAny<string>(),
            LmtConstants.RabbitMqLogsRoutingKey,
            true,
            It.IsAny<BasicProperties>(),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RetryFailedTracesAsync_ShouldSendDueBatch_WhenRetryAllowed()
    {
        var sender = CreateUninitializedSender(maxRetries: 3, maxFailedBatches: 10);

        var connection = new Mock<IConnection>();
        connection.SetupGet(c => c.IsOpen).Returns(true);

        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<BasicProperties>(),
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        SetField(sender, "_connection", connection.Object);
        SetField(sender, "_channel", channel.Object);

        var queue = GetTraceQueue(sender);
        queue.Enqueue(new FailedTraceBatch
        {
            TenantBatches = new Dictionary<string, List<TraceData>>
            {
                ["tenant-a"] = [new TraceData { TraceId = "t", SpanId = "s", TenantId = "tenant-a" }]
            },
            RetryCount = 0,
            NextRetryTime = DateTime.UtcNow.AddSeconds(-1)
        });

        await InvokePrivateAsync(sender, "RetryFailedTracesAsync", DateTime.UtcNow);

        Assert.Empty(queue);
        channel.Verify(c => c.BasicPublishAsync(
            It.IsAny<string>(),
            LmtConstants.RabbitMqTracesRoutingKey,
            true,
            It.IsAny<BasicProperties>(),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
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

        sender.Dispose();

        var disposed = GetField<bool>(sender, "_disposed");
        Assert.True(disposed);
    }

    [Fact]
    public async Task EnsureChannelAsync_ShouldReturn_WhenConnectionAndChannelAlreadyOpen()
    {
        var sender = CreateUninitializedSender(maxRetries: 0, maxFailedBatches: 10);

        var connection = new Mock<IConnection>();
        connection.SetupGet(c => c.IsOpen).Returns(true);

        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);

        SetField(sender, "_connection", connection.Object);
        SetField(sender, "_channel", channel.Object);

        await InvokePrivateAsync(sender, "EnsureChannelAsync");
    }

    [Fact]
    public async Task SendLogsAsync_ShouldPublish_WhenChannelAlreadyOpen()
    {
        var sender = CreateUninitializedSender(maxRetries: 0, maxFailedBatches: 10);

        var connection = new Mock<IConnection>();
        connection.SetupGet(c => c.IsOpen).Returns(true);

        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<BasicProperties>(),
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        SetField(sender, "_connection", connection.Object);
        SetField(sender, "_channel", channel.Object);

        var logs = new List<LogData>
        {
            new() { Message = "ok", Level = "Info", ServiceName = "svc", Timestamp = DateTime.UtcNow }
        };

        await sender.SendLogsAsync(logs);

        Assert.Empty(GetLogQueue(sender));
        channel.Verify(c => c.BasicPublishAsync(
            It.IsAny<string>(),
            LmtConstants.RabbitMqLogsRoutingKey,
            true,
            It.Is<BasicProperties>(p => p.Type == "logs" && p.ContentType == "application/json"),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendTracesAsync_ShouldPublish_WhenChannelAlreadyOpen()
    {
        var sender = CreateUninitializedSender(maxRetries: 0, maxFailedBatches: 10);

        var connection = new Mock<IConnection>();
        connection.SetupGet(c => c.IsOpen).Returns(true);

        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<BasicProperties>(),
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        SetField(sender, "_connection", connection.Object);
        SetField(sender, "_channel", channel.Object);

        var traces = new Dictionary<string, List<TraceData>>
        {
            ["tenant-a"] = [new TraceData { TraceId = "t1", SpanId = "s1", TenantId = "tenant-a" }]
        };

        await sender.SendTracesAsync(traces);

        Assert.Empty(GetTraceQueue(sender));
        channel.Verify(c => c.BasicPublishAsync(
            It.IsAny<string>(),
            LmtConstants.RabbitMqTracesRoutingKey,
            true,
            It.Is<BasicProperties>(p => p.Type == "traces" && p.ContentType == "application/json"),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_ShouldThrow_WhenChannelIsNull()
    {
        var sender = CreateUninitializedSender(maxRetries: 0, maxFailedBatches: 10);
        SetField(sender, "_channel", null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokePrivateAsync(sender, "PublishAsync", "route", new { A = 1 }, "src", "logs"));
    }

    [Fact]
    public void Constructor_ShouldInitializeRecoverySettings()
    {
        using var sender = new LmtRabbitMqSender(
            serviceName: "svc",
            rabbitMqConnectionString: "amqp://guest:guest@localhost:5672",
            maxRetries: 2,
            maxFailedBatches: 5);

        var maxRetries = GetField<int>(sender, "_maxRetries");
        var maxFailedBatches = GetField<int>(sender, "_maxFailedBatches");
        var factory = GetField<ConnectionFactory>(sender, "_factory");

        Assert.Equal(2, maxRetries);
        Assert.Equal(5, maxFailedBatches);
        Assert.True(factory.AutomaticRecoveryEnabled);
        Assert.Equal(TimeSpan.FromSeconds(10), factory.NetworkRecoveryInterval);
    }

    [Fact]
    public void Dispose_ShouldDisposeChannelAndConnection_AndBeIdempotent()
    {
        var sender = CreateUninitializedSender(maxRetries: 0, maxFailedBatches: 10);

        var channel = new Mock<IChannel>();
        var connection = new Mock<IConnection>();
        SetField(sender, "_channel", channel.Object);
        SetField(sender, "_connection", connection.Object);

        sender.Dispose();
        sender.Dispose();

        channel.Verify(c => c.Dispose(), Times.Once);
        connection.Verify(c => c.Dispose(), Times.Once);
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
