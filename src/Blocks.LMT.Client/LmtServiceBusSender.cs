using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Text.Json;

namespace SeliseBlocks.LMT.Client
{
    public class LmtServiceBusSender : ILmtMessageSender
    {
        private readonly string _serviceName;
        private readonly int _maxRetries;
        private readonly int _maxFailedBatches;
        private readonly ConcurrentQueue<FailedLogBatch> _failedLogBatches;
        private readonly ConcurrentQueue<FailedTraceBatch> _failedTraceBatches;
        private readonly Timer _retryTimer;
        private ServiceBusClient? _serviceBusClient;
        private ServiceBusSender? _serviceBusSender;
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

        private async Task RetryFailedBatchesAsync()
        {
            if (!await _retrySemaphore.WaitAsync(0).ConfigureAwait(false))
                return;

            try
            {
                var now = DateTime.UtcNow;

                // Retry failed logs
                await RetryFailedLogsAsync(now).ConfigureAwait(false);

                // Retry failed traces
                await RetryFailedTracesAsync(now).ConfigureAwait(false);
            }
            finally
            {
                _retrySemaphore.Release();
            }
        }

        private async Task RetryFailedLogsAsync(DateTime now)
        {
            var batchesToRetry = new List<FailedLogBatch>();
            var batchesToRequeue = new List<FailedLogBatch>();

            while (_failedLogBatches.TryDequeue(out var failedBatch))
            {
                if (failedBatch.NextRetryTime <= now)
                    batchesToRetry.Add(failedBatch);
                else
                    batchesToRequeue.Add(failedBatch);
            }

            foreach (var batch in batchesToRequeue)
            {
                _failedLogBatches.Enqueue(batch);
            }

            foreach (var failedBatch in batchesToRetry)
            {
                if (failedBatch.RetryCount >= _maxRetries)
                {
                    LmtServiceBusSenderLog.LogBatchExceededRetries(Logger, _maxRetries, failedBatch.Logs.Count);
                    continue;
                }

                LmtServiceBusSenderLog.RetryingLogBatch(Logger, failedBatch.RetryCount + 1, _maxRetries);
                await SendLogsAsync(failedBatch.Logs, failedBatch.RetryCount).ConfigureAwait(false);
            }
        }

        private async Task RetryFailedTracesAsync(DateTime now)
        {
            var batchesToRetry = new List<FailedTraceBatch>();
            var batchesToRequeue = new List<FailedTraceBatch>();

            while (_failedTraceBatches.TryDequeue(out var failedBatch))
            {
                if (failedBatch.NextRetryTime <= now)
                    batchesToRetry.Add(failedBatch);
                else
                    batchesToRequeue.Add(failedBatch);
            }

            foreach (var batch in batchesToRequeue)
            {
                _failedTraceBatches.Enqueue(batch);
            }

            foreach (var failedBatch in batchesToRetry)
            {
                if (failedBatch.RetryCount >= _maxRetries)
                {
                    LmtServiceBusSenderLog.TraceBatchExceededRetries(Logger, _maxRetries);
                    continue;
                }

                LmtServiceBusSenderLog.RetryingTraceBatch(Logger, failedBatch.RetryCount + 1, _maxRetries);
                await SendTracesAsync(failedBatch.TenantBatches, failedBatch.RetryCount).ConfigureAwait(false);
            }
        }

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

    internal static partial class LmtServiceBusSenderLog
    {
        [LoggerMessage(EventId = 5100, Level = LogLevel.Warning, Message = "Service Bus sender not initialized.")]
        public static partial void SenderNotInitialized(ILogger logger);

        [LoggerMessage(EventId = 5101, Level = LogLevel.Warning, Message = "Exception sending logs to Service Bus. Retry {CurrentRetry}/{MaxRetries}.")]
        public static partial void SendingLogsFailed(ILogger logger, Exception exception, int currentRetry, int maxRetries);

        [LoggerMessage(EventId = 5102, Level = LogLevel.Information, Message = "Queued log batch for retry. Failed batches in queue: {QueueCount}.")]
        public static partial void LogBatchQueuedForRetry(ILogger logger, int queueCount);

        [LoggerMessage(EventId = 5103, Level = LogLevel.Warning, Message = "Failed log batch queue is full ({MaxFailedBatches}). Dropping batch.")]
        public static partial void LogBatchQueueFull(ILogger logger, int maxFailedBatches);

        [LoggerMessage(EventId = 5104, Level = LogLevel.Warning, Message = "Exception sending traces to Service Bus. Retry {CurrentRetry}/{MaxRetries}.")]
        public static partial void SendingTracesFailed(ILogger logger, Exception exception, int currentRetry, int maxRetries);

        [LoggerMessage(EventId = 5105, Level = LogLevel.Information, Message = "Queued trace batch for retry. Failed batches in queue: {QueueCount}.")]
        public static partial void TraceBatchQueuedForRetry(ILogger logger, int queueCount);

        [LoggerMessage(EventId = 5106, Level = LogLevel.Warning, Message = "Failed trace batch queue is full ({MaxFailedBatches}). Dropping batch.")]
        public static partial void TraceBatchQueueFull(ILogger logger, int maxFailedBatches);

        [LoggerMessage(EventId = 5107, Level = LogLevel.Warning, Message = "Log batch exceeded max retries ({MaxRetries}). Dropping batch with {LogCount} logs.")]
        public static partial void LogBatchExceededRetries(ILogger logger, int maxRetries, int logCount);

        [LoggerMessage(EventId = 5108, Level = LogLevel.Information, Message = "Retrying failed log batch attempt {Attempt}/{MaxRetries}.")]
        public static partial void RetryingLogBatch(ILogger logger, int attempt, int maxRetries);

        [LoggerMessage(EventId = 5109, Level = LogLevel.Warning, Message = "Trace batch exceeded max retries ({MaxRetries}). Dropping batch.")]
        public static partial void TraceBatchExceededRetries(ILogger logger, int maxRetries);

        [LoggerMessage(EventId = 5110, Level = LogLevel.Information, Message = "Retrying failed trace batch attempt {Attempt}/{MaxRetries}.")]
        public static partial void RetryingTraceBatch(ILogger logger, int attempt, int maxRetries);
    }
}
