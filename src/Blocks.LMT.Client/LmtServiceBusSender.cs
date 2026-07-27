using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Text.Json;

namespace SeliseBlocks.LMT.Client;

public sealed class LmtServiceBusSender : ILmtMessageSender
{
    private readonly string _serviceName;
    private readonly int _maxRetries;
    private readonly int _maxFailedBatches;
    private readonly ConcurrentQueue<FailedLogBatch> _failedLogBatches;
    private readonly ConcurrentQueue<FailedTraceBatch> _failedTraceBatches;
    private readonly Timer _retryTimer;
    private readonly ServiceBusClient? _serviceBusClient;
    private readonly ServiceBusSender? _serviceBusSender;
    private readonly SemaphoreSlim _retrySemaphore = new SemaphoreSlim(1, 1);
    private bool _disposed;
    private readonly ILogger<LmtServiceBusSender>? _logger;
    private ILogger Logger => _logger ?? NullLogger<LmtServiceBusSender>.Instance;

    public LmtServiceBusSender(
        string serviceName,
        string serviceBusConnectionString,
        int maxRetries = 3,
        int maxFailedBatches = 100,
        ILogger<LmtServiceBusSender>? logger = null)
    {
        _serviceName = serviceName;
        _maxRetries = maxRetries;
        _maxFailedBatches = maxFailedBatches;
        _logger = logger ?? NullLogger<LmtServiceBusSender>.Instance;

        _failedLogBatches = new ConcurrentQueue<FailedLogBatch>();
        _failedTraceBatches = new ConcurrentQueue<FailedTraceBatch>();

        if (!string.IsNullOrWhiteSpace(serviceBusConnectionString))
        {
            _serviceBusClient = new ServiceBusClient(serviceBusConnectionString);
            _serviceBusSender = _serviceBusClient.CreateSender(LmtConstants.GetTopicName(serviceName));
        }

        _retryTimer = new Timer(async _ => await RetryFailedBatchesAsync().ConfigureAwait(false), null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public async Task SendLogsAsync(List<LogData> logs, int retryCount = 0)
    {
        if (_serviceBusSender == null)
        {
            LmtServiceBusSenderLog.SenderNotInitialized(Logger);
            return;
        }

        int currentRetry = 0;

        while (currentRetry <= _maxRetries)
        {
            try
            {
                var payload = new
                {
                    Type = "logs",
                    ServiceName = _serviceName,
                    Data = logs
                };

                var json = JsonSerializer.Serialize(payload);
                var timestamp = DateTime.UtcNow;
                var messageId = $"logs_{_serviceName}_{timestamp:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}";

                var message = new ServiceBusMessage(json)
                {
                    ContentType = "application/json",
                    MessageId = messageId,
                    CorrelationId = LmtConstants.LogSubscription,
                    ApplicationProperties =
                    {
                        { "serviceName", _serviceName },
                        { "timestamp", timestamp.ToString("o") },
                        { "source", "LogsSender" },
                        { "type", "logs" }
                    }
                };

                await _serviceBusSender.SendMessageAsync(message).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                LmtServiceBusSenderLog.SendingLogsFailed(Logger, ex, currentRetry, _maxRetries);
            }

            currentRetry++;

            if (currentRetry <= _maxRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, currentRetry - 1));
                await Task.Delay(delay).ConfigureAwait(false);
            }
        }

        // Queue for later retry
        if (_failedLogBatches.Count < _maxFailedBatches)
        {
            var failedBatch = new FailedLogBatch
            {
                Logs = logs,
                RetryCount = retryCount + 1,
                NextRetryTime = DateTime.UtcNow.AddMinutes(Math.Pow(2, retryCount))
            };

            _failedLogBatches.Enqueue(failedBatch);
            LmtServiceBusSenderLog.LogBatchQueuedForRetry(Logger, _failedLogBatches.Count);
        }
        else
        {
            LmtServiceBusSenderLog.LogBatchQueueFull(Logger, _maxFailedBatches);
        }
    }

    public async Task SendTracesAsync(Dictionary<string, List<TraceData>> tenantBatches, int retryCount = 0)
    {
        if (_serviceBusSender == null)
        {
            LmtServiceBusSenderLog.SenderNotInitialized(Logger);
            return;
        }

        int currentRetry = 0;

        while (currentRetry <= _maxRetries)
        {
            try
            {
                var payload = new
                {
                    Type = "traces",
                    ServiceName = _serviceName,
                    Data = tenantBatches
                };

                var json = JsonSerializer.Serialize(payload);
                var timestamp = DateTime.UtcNow;
                var messageId = $"traces_{_serviceName}_{timestamp:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}";

                var message = new ServiceBusMessage(json)
                {
                    ContentType = "application/json",
                    MessageId = messageId,
                    CorrelationId = LmtConstants.TraceSubscription,
                    ApplicationProperties =
                    {
                        { "serviceName", _serviceName },
                        { "timestamp", timestamp.ToString("o") },
                        { "source", "TracesSender" },
                        { "type", "traces" }
                    }
                };

                await _serviceBusSender.SendMessageAsync(message).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                LmtServiceBusSenderLog.SendingTracesFailed(Logger, ex, currentRetry, _maxRetries);
            }

            currentRetry++;

            if (currentRetry <= _maxRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, currentRetry - 1));
                await Task.Delay(delay).ConfigureAwait(false);
            }
        }

        // Queue for later retry
        if (_failedTraceBatches.Count < _maxFailedBatches)
        {
            var failedBatch = new FailedTraceBatch
            {
                TenantBatches = tenantBatches,
                RetryCount = retryCount + 1,
                NextRetryTime = DateTime.UtcNow.AddMinutes(Math.Pow(2, retryCount))
            };

            _failedTraceBatches.Enqueue(failedBatch);
            LmtServiceBusSenderLog.TraceBatchQueuedForRetry(Logger, _failedTraceBatches.Count);
        }
        else
        {
            LmtServiceBusSenderLog.TraceBatchQueueFull(Logger, _maxFailedBatches);
        }
    }

    private Task RetryFailedBatchesAsync() =>
        LmtFailedBatchRetryHelper.RetryFailedBatchesAsync(_retrySemaphore, RetryFailedLogsAsync, RetryFailedTracesAsync);

    private Task RetryFailedLogsAsync(DateTime now) =>
        LmtFailedBatchRetryHelper.RetryDueBatchesAsync(
            _failedLogBatches,
            now,
            _maxRetries,
            batch => batch.NextRetryTime,
            batch => batch.RetryCount,
            batch => LmtServiceBusSenderLog.LogBatchExceededRetries(Logger, _maxRetries, batch.Logs.Count),
            async batch =>
            {
                LmtServiceBusSenderLog.RetryingLogBatch(Logger, batch.RetryCount + 1, _maxRetries);
                await SendLogsAsync(batch.Logs, batch.RetryCount).ConfigureAwait(false);
            });

    private Task RetryFailedTracesAsync(DateTime now) =>
        LmtFailedBatchRetryHelper.RetryDueBatchesAsync(
            _failedTraceBatches,
            now,
            _maxRetries,
            batch => batch.NextRetryTime,
            batch => batch.RetryCount,
            _ => LmtServiceBusSenderLog.TraceBatchExceededRetries(Logger, _maxRetries),
            async batch =>
            {
                LmtServiceBusSenderLog.RetryingTraceBatch(Logger, batch.RetryCount + 1, _maxRetries);
                await SendTracesAsync(batch.TenantBatches, batch.RetryCount).ConfigureAwait(false);
            });

    public void Dispose()
    {
        if (_disposed) return;

        _retryTimer?.Dispose();
        RetryFailedBatchesAsync().GetAwaiter().GetResult();
        _retrySemaphore?.Dispose();
        _serviceBusSender?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _serviceBusClient?.DisposeAsync().AsTask().GetAwaiter().GetResult();

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

internal static class LmtServiceBusSenderLog
{
    private static readonly Action<ILogger, Exception?> SenderNotInitializedMessage =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(5100),
            "Service Bus sender not initialized.");

    private static readonly Action<ILogger, int, int, Exception?> SendingLogsFailedMessage =
        LoggerMessage.Define<int, int>(
            LogLevel.Warning,
            new EventId(5101),
            "Exception sending logs to Service Bus. Retry {CurrentRetry}/{MaxRetries}.");

    private static readonly Action<ILogger, int, Exception?> LogBatchQueuedForRetryMessage =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(5102),
            "Queued log batch for retry. Failed batches in queue: {QueueCount}.");

    private static readonly Action<ILogger, int, Exception?> LogBatchQueueFullMessage =
        LoggerMessage.Define<int>(
            LogLevel.Warning,
            new EventId(5103),
            "Failed log batch queue is full ({MaxFailedBatches}). Dropping batch.");

    private static readonly Action<ILogger, int, int, Exception?> SendingTracesFailedMessage =
        LoggerMessage.Define<int, int>(
            LogLevel.Warning,
            new EventId(5104),
            "Exception sending traces to Service Bus. Retry {CurrentRetry}/{MaxRetries}.");

    private static readonly Action<ILogger, int, Exception?> TraceBatchQueuedForRetryMessage =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(5105),
            "Queued trace batch for retry. Failed batches in queue: {QueueCount}.");

    private static readonly Action<ILogger, int, Exception?> TraceBatchQueueFullMessage =
        LoggerMessage.Define<int>(
            LogLevel.Warning,
            new EventId(5106),
            "Failed trace batch queue is full ({MaxFailedBatches}). Dropping batch.");

    private static readonly Action<ILogger, int, int, Exception?> LogBatchExceededRetriesMessage =
        LoggerMessage.Define<int, int>(
            LogLevel.Warning,
            new EventId(5107),
            "Log batch exceeded max retries ({MaxRetries}). Dropping batch with {LogCount} logs.");

    private static readonly Action<ILogger, int, int, Exception?> RetryingLogBatchMessage =
        LoggerMessage.Define<int, int>(
            LogLevel.Information,
            new EventId(5108),
            "Retrying failed log batch attempt {Attempt}/{MaxRetries}.");

    private static readonly Action<ILogger, int, Exception?> TraceBatchExceededRetriesMessage =
        LoggerMessage.Define<int>(
            LogLevel.Warning,
            new EventId(5109),
            "Trace batch exceeded max retries ({MaxRetries}). Dropping batch.");

    private static readonly Action<ILogger, int, int, Exception?> RetryingTraceBatchMessage =
        LoggerMessage.Define<int, int>(
            LogLevel.Information,
            new EventId(5110),
            "Retrying failed trace batch attempt {Attempt}/{MaxRetries}.");

    public static void SenderNotInitialized(ILogger logger) =>
        SenderNotInitializedMessage(logger, null);

    public static void SendingLogsFailed(ILogger logger, Exception exception, int currentRetry, int maxRetries) =>
        SendingLogsFailedMessage(logger, currentRetry, maxRetries, exception);

    public static void LogBatchQueuedForRetry(ILogger logger, int queueCount) =>
        LogBatchQueuedForRetryMessage(logger, queueCount, null);

    public static void LogBatchQueueFull(ILogger logger, int maxFailedBatches) =>
        LogBatchQueueFullMessage(logger, maxFailedBatches, null);

    public static void SendingTracesFailed(ILogger logger, Exception exception, int currentRetry, int maxRetries) =>
        SendingTracesFailedMessage(logger, currentRetry, maxRetries, exception);

    public static void TraceBatchQueuedForRetry(ILogger logger, int queueCount) =>
        TraceBatchQueuedForRetryMessage(logger, queueCount, null);

    public static void TraceBatchQueueFull(ILogger logger, int maxFailedBatches) =>
        TraceBatchQueueFullMessage(logger, maxFailedBatches, null);

    public static void LogBatchExceededRetries(ILogger logger, int maxRetries, int logCount) =>
        LogBatchExceededRetriesMessage(logger, maxRetries, logCount, null);

    public static void RetryingLogBatch(ILogger logger, int attempt, int maxRetries) =>
        RetryingLogBatchMessage(logger, attempt, maxRetries, null);

    public static void TraceBatchExceededRetries(ILogger logger, int maxRetries) =>
        TraceBatchExceededRetriesMessage(logger, maxRetries, null);

    public static void RetryingTraceBatch(ILogger logger, int attempt, int maxRetries) =>
        RetryingTraceBatchMessage(logger, attempt, maxRetries, null);
}
