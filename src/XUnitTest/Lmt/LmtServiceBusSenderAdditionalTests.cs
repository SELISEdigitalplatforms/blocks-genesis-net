using Azure.Messaging.ServiceBus;
using Moq;
using SeliseBlocks.LMT.Client;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace XUnitTest.Lmt;

public class LmtServiceBusSenderAdditionalTests
{
    [Fact]
    public async Task SendLogsAsync_ShouldSendMessage_WithExpectedMetadata()
    {
        ServiceBusMessage? captured = null;
        var mockSbSender = new Mock<ServiceBusSender>();
        mockSbSender
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, CancellationToken>((msg, _) => captured = msg)
            .Returns(Task.CompletedTask);

        var sender = CreateSenderWithMockServiceBus(mockSbSender.Object, maxRetries: 0, maxFailedBatches: 10);

        await sender.SendLogsAsync([
            new LogData { Message = "test", Level = "Info", ServiceName = "svc", Timestamp = DateTime.UtcNow }
        ]);

        mockSbSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(captured);
        Assert.Equal("application/json", captured!.ContentType);
        Assert.Equal(LmtConstants.LogSubscription, captured.CorrelationId);
        Assert.Equal("logs", captured.ApplicationProperties["type"]);
    }

    [Fact]
    public async Task SendTracesAsync_ShouldSendMessage_WithExpectedMetadata()
    {
        ServiceBusMessage? captured = null;
        var mockSbSender = new Mock<ServiceBusSender>();
        mockSbSender
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, CancellationToken>((msg, _) => captured = msg)
            .Returns(Task.CompletedTask);

        var sender = CreateSenderWithMockServiceBus(mockSbSender.Object, maxRetries: 0, maxFailedBatches: 10);

        await sender.SendTracesAsync(new Dictionary<string, List<TraceData>>
        {
            ["t1"] = [new TraceData { TraceId = "tr-1", SpanId = "sp-1" }]
        });

        mockSbSender.Verify(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(captured);
        Assert.Equal("application/json", captured!.ContentType);
        Assert.Equal(LmtConstants.TraceSubscription, captured.CorrelationId);
        Assert.Equal("traces", captured.ApplicationProperties["type"]);
    }

    [Fact]
    public async Task SendLogsAsync_ShouldQueueFailedBatch_WhenSendThrows()
    {
        var mockSbSender = new Mock<ServiceBusSender>();
        mockSbSender
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceBusException("send failed", ServiceBusFailureReason.GeneralError));

        var sender = CreateSenderWithMockServiceBus(mockSbSender.Object, maxRetries: 0, maxFailedBatches: 10);

        await sender.SendLogsAsync([
            new LogData { Message = "test", Level = "Info", ServiceName = "svc", Timestamp = DateTime.UtcNow }
        ]);

        var queue = GetLogQueue(sender);
        Assert.Single(queue);
    }

    [Fact]
    public async Task SendLogsAsync_ShouldDropBatch_WhenQueueFull()
    {
        var mockSbSender = new Mock<ServiceBusSender>();
        mockSbSender
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceBusException("fail", ServiceBusFailureReason.GeneralError));

        var sender = CreateSenderWithMockServiceBus(mockSbSender.Object, maxRetries: 0, maxFailedBatches: 1);

        // Fill the queue
        GetLogQueue(sender).Enqueue(new FailedLogBatch
        {
            Logs = [new LogData()],
            RetryCount = 0,
            NextRetryTime = DateTime.UtcNow.AddMinutes(5)
        });

        // This one should be dropped
        await sender.SendLogsAsync([new LogData { Message = "dropped" }]);

        Assert.Single(GetLogQueue(sender));
    }

    [Fact]
    public async Task SendTracesAsync_ShouldQueueFailedBatch_WhenSendThrows()
    {
        var mockSbSender = new Mock<ServiceBusSender>();
        mockSbSender
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceBusException("send failed", ServiceBusFailureReason.GeneralError));

        var sender = CreateSenderWithMockServiceBus(mockSbSender.Object, maxRetries: 0, maxFailedBatches: 10);

        await sender.SendTracesAsync(new Dictionary<string, List<TraceData>>
        {
            ["t1"] = [new TraceData { TraceId = "tr-1", SpanId = "sp-1" }]
        });

        var queue = GetTraceQueue(sender);
        Assert.Single(queue);
    }

    [Fact]
    public async Task SendTracesAsync_ShouldDropBatch_WhenQueueFull()
    {
        var mockSbSender = new Mock<ServiceBusSender>();
        mockSbSender
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ServiceBusException("fail", ServiceBusFailureReason.GeneralError));

        var sender = CreateSenderWithMockServiceBus(mockSbSender.Object, maxRetries: 0, maxFailedBatches: 1);

        GetTraceQueue(sender).Enqueue(new FailedTraceBatch
        {
            TenantBatches = new Dictionary<string, List<TraceData>> { ["x"] = [] },
            RetryCount = 0,
            NextRetryTime = DateTime.UtcNow.AddMinutes(5)
        });

        await sender.SendTracesAsync(new Dictionary<string, List<TraceData>> { ["y"] = [] });

        Assert.Single(GetTraceQueue(sender));
    }

    [Fact]
    public async Task SendLogsAsync_ShouldRetryBeforeQueuing()
    {
        int callCount = 0;
        var mockSbSender = new Mock<ServiceBusSender>();
        mockSbSender
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback(() => callCount++)
            .ThrowsAsync(new Exception("fail"));

        var sender = CreateSenderWithMockServiceBus(mockSbSender.Object, maxRetries: 1, maxFailedBatches: 10);

        await sender.SendLogsAsync([new LogData { Message = "test" }]);

        // maxRetries=1 means: attempt 0, then retry 1 = 2 total calls
        Assert.Equal(2, callCount);
        Assert.Single(GetLogQueue(sender));
    }

    [Fact]
    public async Task RetryFailedTracesAsync_ShouldDropBatch_WhenMaxRetriesExceeded()
    {
        var sender = CreateSenderWithMockServiceBus(null, maxRetries: 1, maxFailedBatches: 10);
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
    public async Task RetryFailedLogsAsync_ShouldDrainDueBatches_WhenRetryAllowed()
    {
        var sender = CreateSenderWithMockServiceBus(null, maxRetries: 3, maxFailedBatches: 10);
        var queue = GetLogQueue(sender);
        queue.Enqueue(new FailedLogBatch
        {
            Logs = [new LogData { Message = "retry" }],
            RetryCount = 0,
            NextRetryTime = DateTime.UtcNow.AddMinutes(-1)
        });

        await InvokePrivateAsync(sender, "RetryFailedLogsAsync", DateTime.UtcNow);

        Assert.Empty(queue);
    }

    [Fact]
    public void Dispose_ShouldBeIdempotent()
    {
        var sender = new LmtServiceBusSender("svc", string.Empty);
        sender.Dispose();
        sender.Dispose(); // second call should be no-op
    }

    [Fact]
    public void Type_ShouldImplementILmtMessageSender()
    {
        Assert.True(typeof(ILmtMessageSender).IsAssignableFrom(typeof(LmtServiceBusSender)));
    }

    // --- Helpers ---

    private static LmtServiceBusSender CreateSenderWithMockServiceBus(
        ServiceBusSender? sbSender,
        int maxRetries,
        int maxFailedBatches)
    {
        var sender = (LmtServiceBusSender)RuntimeHelpers.GetUninitializedObject(typeof(LmtServiceBusSender));
        SetField(sender, "_serviceName", "test-svc");
        SetField(sender, "_maxRetries", maxRetries);
        SetField(sender, "_maxFailedBatches", maxFailedBatches);
        SetField(sender, "_failedLogBatches", new ConcurrentQueue<FailedLogBatch>());
        SetField(sender, "_failedTraceBatches", new ConcurrentQueue<FailedTraceBatch>());
        SetField(sender, "_retrySemaphore", new SemaphoreSlim(1, 1));
        SetField(sender, "_disposed", false);
        SetField(sender, "_retryTimer", new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite));
        SetField(sender, "_serviceBusSender", sbSender);
        return sender;
    }

    private static ConcurrentQueue<FailedLogBatch> GetLogQueue(LmtServiceBusSender sender)
        => GetField<ConcurrentQueue<FailedLogBatch>>(sender, "_failedLogBatches");

    private static ConcurrentQueue<FailedTraceBatch> GetTraceQueue(LmtServiceBusSender sender)
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
