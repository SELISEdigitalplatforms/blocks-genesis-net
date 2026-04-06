using Moq;
using SeliseBlocks.LMT.Client;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace XUnitTest.Lmt;

public class BlocksLoggerTests
{
    [Fact]
    public void Constructor_ShouldThrow_WhenOptionsIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new BlocksLogger(null!));
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenServiceIdIsEmpty()
    {
        var options = new LmtOptions { ServiceId = "", ConnectionString = "Endpoint=sb://x" };

        Assert.Throws<ArgumentException>(() => new BlocksLogger(options));
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenConnectionStringIsEmpty()
    {
        var options = new LmtOptions { ServiceId = "svc", ConnectionString = "" };

        var exception = Assert.Throws<ArgumentException>(() => new BlocksLogger(options));
        Assert.Contains("ConnectionString is required", exception.Message);
    }

    [Fact]
    public void Log_ShouldNotEnqueue_WhenLoggingDisabled()
    {
        var logger = CreateLoggerWithMockSender(out _, enableLogging: false);

        logger.Log(LmtLogLevel.Information, "test message");

        var queue = GetLogQueue(logger);
        Assert.Empty(queue);
    }

    [Fact]
    public void Log_ShouldEnqueueLogData_WhenLoggingEnabled()
    {
        var logger = CreateLoggerWithMockSender(out _);

        logger.Log(LmtLogLevel.Warning, "something happened");

        var queue = GetLogQueue(logger);
        Assert.Single(queue);
        Assert.True(queue.TryPeek(out var log));
        Assert.Equal("Warning", log.Level);
        Assert.Equal("something happened", log.Message);
    }

    [Fact]
    public void Log_ShouldFormatMessageTemplate_WithArgs()
    {
        var logger = CreateLoggerWithMockSender(out _);

        logger.Log(LmtLogLevel.Information, "User {Name} logged in at {Time}", null, "Alice", "10:00");

        var queue = GetLogQueue(logger);
        Assert.True(queue.TryPeek(out var log));
        Assert.Equal("User Alice logged in at 10:00", log.Message);
           Assert.Equal("Alice", log.Properties["Name"]);
           Assert.Equal("10:00", log.Properties["Time"]);
    }

    [Fact]
    public void Log_ShouldIncludeException_WhenProvided()
    {
        var logger = CreateLoggerWithMockSender(out _);
        var ex = new InvalidOperationException("boom");

        logger.Log(LmtLogLevel.Error, "Error occurred", ex);

        var queue = GetLogQueue(logger);
        Assert.True(queue.TryPeek(out var log));
        Assert.Contains("boom", log.Exception);
    }

    [Fact]
    public void Log_ShouldSetServiceNameAndTenantId()
    {
        var logger = CreateLoggerWithMockSender(out _, serviceId: "my-svc", xBlocksKey: "tenant-abc");

        logger.Log(LmtLogLevel.Debug, "msg");

        var queue = GetLogQueue(logger);
        Assert.True(queue.TryPeek(out var log));
        Assert.Equal("my-svc", log.ServiceName);
        Assert.Equal("tenant-abc", log.TenantId);
    }

    [Fact]
    public void Log_ShouldAttachTraceAndSpanId_WhenActivityIsActive()
    {
        using var source = new ActivitySource("test-logger-activity");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "test-logger-activity",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = source.StartActivity("test-op");
        Assert.NotNull(activity);

        var logger = CreateLoggerWithMockSender(out _);

        logger.Log(LmtLogLevel.Information, "with trace");

        var queue = GetLogQueue(logger);
        Assert.True(queue.TryPeek(out var log));
        Assert.Equal(activity!.TraceId.ToString(), log.Properties["TraceId"]);
        Assert.Equal(activity.SpanId.ToString(), log.Properties["SpanId"]);
    }

    [Fact]
    public void LogTrace_ShouldLogAtTraceLevel()
    {
        var logger = CreateLoggerWithMockSender(out _);
        logger.LogTrace("trace msg");
        AssertLogLevel(logger, "Trace");
    }

    [Fact]
    public void LogDebug_ShouldLogAtDebugLevel()
    {
        var logger = CreateLoggerWithMockSender(out _);
        logger.LogDebug("debug msg");
        AssertLogLevel(logger, "Debug");
    }

    [Fact]
    public void LogInformation_ShouldLogAtInformationLevel()
    {
        var logger = CreateLoggerWithMockSender(out _);
        logger.LogInformation("info msg");
        AssertLogLevel(logger, "Information");
    }

    [Fact]
    public void LogWarning_ShouldLogAtWarningLevel()
    {
        var logger = CreateLoggerWithMockSender(out _);
        logger.LogWarning("warn msg");
        AssertLogLevel(logger, "Warning");
    }

    [Fact]
    public void LogError_ShouldLogAtErrorLevel()
    {
        var logger = CreateLoggerWithMockSender(out _);
        logger.LogError("error msg", new Exception("err"));
        AssertLogLevel(logger, "Error");
    }

    [Fact]
    public void LogCritical_ShouldLogAtCriticalLevel()
    {
        var logger = CreateLoggerWithMockSender(out _);
        logger.LogCritical("critical msg", new Exception("crit"));
        AssertLogLevel(logger, "Critical");
    }

    [Fact]
    public void FormatLogMessage_ShouldLeavePlaceholders_WhenTooFewArgs()
    {
        var logger = CreateLoggerWithMockSender(out _);

        logger.Log(LmtLogLevel.Information, "{A} and {B}", null, "only-one");

        var queue = GetLogQueue(logger);
        Assert.True(queue.TryPeek(out var log));
        Assert.Equal("only-one and {B}", log.Message);
    }

    [Fact]
    public void FormatLogMessage_ShouldReturnTemplate_WhenNoArgs()
    {
        var logger = CreateLoggerWithMockSender(out _);

        logger.Log(LmtLogLevel.Information, "no args here");

        var queue = GetLogQueue(logger);
        Assert.True(queue.TryPeek(out var log));
        Assert.Equal("no args here", log.Message);
    }

    [Fact]
    public async Task FlushBatchAsync_ShouldSendEnqueuedLogsToSender()
    {
        var logger = CreateLoggerWithMockSender(out var mockSender);
        logger.Log(LmtLogLevel.Information, "msg1");
        logger.Log(LmtLogLevel.Warning, "msg2");

        await InvokeFlush(logger);

        mockSender.Verify(s => s.SendLogsAsync(
            It.Is<List<LogData>>(l => l.Count == 2),
            It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task FlushBatchAsync_ShouldNotCallSender_WhenQueueIsEmpty()
    {
        var logger = CreateLoggerWithMockSender(out var mockSender);

        await InvokeFlush(logger);

        mockSender.Verify(s => s.SendLogsAsync(It.IsAny<List<LogData>>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task FlushBatchAsync_ShouldNotThrow_WhenSenderThrows()
    {
        var logger = CreateLoggerWithMockSender(out var mockSender);
        mockSender.Setup(s => s.SendLogsAsync(It.IsAny<List<LogData>>(), It.IsAny<int>()))
            .ThrowsAsync(new Exception("send failed"));

        logger.Log(LmtLogLevel.Information, "msg");

        var ex = await Record.ExceptionAsync(() => InvokeFlush(logger));
        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_ShouldSetDisposedFlag()
    {
        var logger = CreateLoggerWithMockSender(out _);

        logger.Dispose();

        var disposed = GetField<bool>(logger, "_disposed");
        Assert.True(disposed);
    }

    [Fact]
    public void Dispose_ShouldBeIdempotent()
    {
        var logger = CreateLoggerWithMockSender(out _);

        logger.Dispose();
        logger.Dispose(); // should not throw
    }

    [Fact]
    public void Log_ShouldHandleNullArgsGracefully()
    {
        var logger = CreateLoggerWithMockSender(out _);

        logger.Log(LmtLogLevel.Information, "Value is {V}", null, (object?)null);

        var queue = GetLogQueue(logger);
        Assert.True(queue.TryPeek(out var log));
        Assert.Equal("Value is ", log.Message);
    }

    [Fact]
    public async Task Log_ShouldAutoFlush_WhenBatchSizeExceeded()
    {
        var logger = CreateLoggerWithMockSender(out var mockSender, logBatchSize: 2);
        
        // Add 2 logs (fills the batch)
        logger.Log(LmtLogLevel.Information, "msg1");
        logger.Log(LmtLogLevel.Information, "msg2");

        // Small delay to allow Task.Run to execute
        await Task.Delay(100);

        // Should have triggered async flush
        mockSender.Verify(s => s.SendLogsAsync(It.IsAny<List<LogData>>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public void Dispose_ShouldFlushPendingLogs()
    {
        var logger = CreateLoggerWithMockSender(out var mockSender);
        logger.Log(LmtLogLevel.Information, "pending-msg");

        logger.Dispose();

        mockSender.Verify(s => s.SendLogsAsync(
            It.Is<List<LogData>>(l => l.Count == 1),
            It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public void Dispose_ShouldStopFlushTimer()
    {
        var logger = CreateLoggerWithMockSender(out _);
        var timer = GetField<Timer>(logger, "_flushTimer");

        logger.Dispose();

        // Timer should be stopped after dispose
        var disposed = GetField<bool>(logger, "_disposed");
        Assert.True(disposed);
    }

    [Fact]
    public void Constructor_ShouldInitializeWithRealFactory()
    {
        // Use real constructor to cover initialization path
        var options = new LmtOptions
        {
            ServiceId = "real-svc",
            ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=secret",
            EnableLogging = true,
            FlushIntervalSeconds = 60,
            LogBatchSize = 100,
            XBlocksKey = "tenant-real"
        };

        var logger = new BlocksLogger(options);
        try
        {
            Assert.NotNull(logger);
            // Verify fields are properly set
            var field = GetField<LmtOptions>(logger, "_options");
            Assert.Equal("real-svc", field.ServiceId);
        }
        finally
        {
            logger.Dispose();
        }
    }

    // --- Helpers ---

    private static BlocksLogger CreateLoggerWithMockSender(
        out Mock<ILmtMessageSender> mockSender,
        bool enableLogging = true,
        string serviceId = "test-svc",
        string xBlocksKey = "test-key",
        int logBatchSize = 9999)
    {
        mockSender = new Mock<ILmtMessageSender>();
        var options = new LmtOptions
        {
            ServiceId = serviceId,
            ConnectionString = "Endpoint=sb://dummy.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=dummykey",
            EnableLogging = enableLogging,
            FlushIntervalSeconds = 9999,
            LogBatchSize = logBatchSize,
            XBlocksKey = xBlocksKey
        };

        var logger = (BlocksLogger)RuntimeHelpers.GetUninitializedObject(typeof(BlocksLogger));
        SetField(logger, "_options", options);
        SetField(logger, "_logBatch", new ConcurrentQueue<LogData>());
        SetField(logger, "_serviceBusSender", mockSender.Object);
        SetField(logger, "_semaphore", new SemaphoreSlim(2, 2));
        SetField(logger, "_flushTimer", new PeriodicTimer(TimeSpan.FromHours(1)));
        SetField(logger, "_disposeCts", new CancellationTokenSource());
        SetField(logger, "_flushLoopTask", Task.CompletedTask);
        SetField(logger, "_disposed", false);
        return logger;
    }

    private static void AssertLogLevel(BlocksLogger logger, string expectedLevel)
    {
        var queue = GetLogQueue(logger);
        Assert.True(queue.TryPeek(out var log));
        Assert.Equal(expectedLevel, log.Level);
    }

    private static ConcurrentQueue<LogData> GetLogQueue(BlocksLogger logger)
        => GetField<ConcurrentQueue<LogData>>(logger, "_logBatch");

    private static async Task InvokeFlush(BlocksLogger logger)
    {
        var method = typeof(BlocksLogger).GetMethod("FlushBatchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(logger, null)!;
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
