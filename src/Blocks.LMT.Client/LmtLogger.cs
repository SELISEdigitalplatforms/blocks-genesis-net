using Azure.Messaging.ServiceBus;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Blocks.LMT.Client
{
    public class LmtLogger : ILmtLogger, IDisposable
    {
        private readonly LmtOptions _options;
        private readonly ConcurrentQueue<LogData> _logBatch;
        private readonly ConcurrentQueue<FailedLogBatch> _failedBatches;
        private readonly Timer _flushTimer;
        private readonly Timer _retryTimer;
        private ServiceBusClient? _serviceBusClient;
        private ServiceBusSender? _serviceBusSender;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _retrySemaphore = new SemaphoreSlim(1, 1);
        private bool _disposed;

        public LmtLogger(LmtOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            if (string.IsNullOrWhiteSpace(_options.ServiceName))
                throw new ArgumentException("ServiceName is required", nameof(options));

            if (string.IsNullOrWhiteSpace(_options.LogsServiceBusConnectionString))
                throw new ArgumentException("ServiceBusConnectionString is required", nameof(options));

            _logBatch = new ConcurrentQueue<LogData>();
            _failedBatches = new ConcurrentQueue<FailedLogBatch>();

            // Initialize Service Bus
            _serviceBusClient = new ServiceBusClient(_options.LogsServiceBusConnectionString);
            _serviceBusSender = _serviceBusClient.CreateSender("blocks-lmt-sevice-logs");

            var flushInterval = TimeSpan.FromSeconds(_options.FlushIntervalSeconds);
            _flushTimer = new Timer(async _ => await FlushBatchAsync(), null, flushInterval, flushInterval);
            _retryTimer = new Timer(async _ => await RetryFailedBatchesAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        public void Log(LmtLogLevel level, string message, Exception exception = null, Dictionary<string, object> properties = null)
        {
            if (!_options.EnableLogging) return;

            var activity = Activity.Current;
            var logData = new LogData
            {
                Timestamp = DateTime.UtcNow,
                Level = level.ToString(),
                Message = message,
                Exception = exception?.ToString() ?? string.Empty,
                ServiceName = _options.ServiceName,
                Properties = properties ?? new Dictionary<string, object>()
            };

            // Add trace context if available
            if (activity != null)
            {
                logData.Properties["TraceId"] = activity.TraceId.ToString();
                logData.Properties["SpanId"] = activity.SpanId.ToString();
            }

            _logBatch.Enqueue(logData);

            if (_logBatch.Count >= _options.LogBatchSize)
            {
                Task.Run(() => FlushBatchAsync());
            }
        }

        public void LogTrace(string message, Dictionary<string, object> properties = null)
            => Log(LmtLogLevel.Trace, message, null, properties);

        public void LogDebug(string message, Dictionary<string, object> properties = null)
            => Log(LmtLogLevel.Debug, message, null, properties);

        public void LogInformation(string message, Dictionary<string, object> properties = null)
            => Log(LmtLogLevel.Information, message, null, properties);

        public void LogWarning(string message, Dictionary<string, object> properties = null)
            => Log(LmtLogLevel.Warning, message, null, properties);

        public void LogError(string message, Exception exception = null, Dictionary<string, object> properties = null)
            => Log(LmtLogLevel.Error, message, exception, properties);

        public void LogCritical(string message, Exception exception = null, Dictionary<string, object> properties = null)
            => Log(LmtLogLevel.Critical, message, exception, properties);

        private async Task FlushBatchAsync()
        {
            await _semaphore.WaitAsync();
            try
            {
                var logs = new List<LogData>();
                while (_logBatch.TryDequeue(out var log))
                {
                    logs.Add(log);
                }

                if (logs.Count > 0)
                {
                    await SendToServiceBusAsync(logs);
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private async Task SendToServiceBusAsync(List<LogData> logs, int retryCount = 0)
        {
            int currentRetry = 0;

            while (currentRetry <= _options.MaxRetries)
            {
                try
                {
                    var payload = new
                    {
                        Type = "logs",
                        ServiceName = _options.ServiceName,
                        Data = logs
                    };

                    var json = JsonSerializer.Serialize(payload);
                    var timestamp = DateTime.UtcNow;
                    var messageId = $"logs_{_options.ServiceName}_{timestamp:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}";

                    var message = new ServiceBusMessage(json)
                    {
                        ContentType = "application/json",
                        MessageId = messageId,
                        CorrelationId = _options.ServiceName,
                        ApplicationProperties =
                        {
                            { "serviceName", _options.ServiceName },
                            { "timestamp", timestamp.ToString("o") },
                            { "source", "LogsClient" },
                            { "type", "logs" }
                        }
                    };

                    await _serviceBusSender!.SendMessageAsync(message);
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Exception sending logs to Service Bus: {ex.Message}");
                }

                currentRetry++;

                if (currentRetry <= _options.MaxRetries)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, currentRetry - 1));
                    await Task.Delay(delay);
                }
            }

            // Queue for later retry
            if (_failedBatches.Count < _options.MaxFailedBatches)
            {
                var failedBatch = new FailedLogBatch
                {
                    Logs = logs,
                    RetryCount = retryCount + 1,
                    NextRetryTime = DateTime.UtcNow.AddMinutes(Math.Pow(2, retryCount))
                };

                _failedBatches.Enqueue(failedBatch);
            }
        }

        private async Task RetryFailedBatchesAsync()
        {
            if (!await _retrySemaphore.WaitAsync(0))
                return;

            try
            {
                var now = DateTime.UtcNow;
                var batchesToRetry = new List<FailedLogBatch>();
                var batchesToRequeue = new List<FailedLogBatch>();

                while (_failedBatches.TryDequeue(out var failedBatch))
                {
                    if (failedBatch.NextRetryTime <= now)
                        batchesToRetry.Add(failedBatch);
                    else
                        batchesToRequeue.Add(failedBatch);
                }

                foreach (var batch in batchesToRequeue)
                {
                    _failedBatches.Enqueue(batch);
                }

                foreach (var failedBatch in batchesToRetry)
                {
                    if (failedBatch.RetryCount >= _options.MaxRetries)
                        continue;

                    await SendToServiceBusAsync(failedBatch.Logs, failedBatch.RetryCount);
                }
            }
            finally
            {
                _retrySemaphore.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _flushTimer?.Dispose();
            _retryTimer?.Dispose();
            _semaphore?.Dispose();
            _retrySemaphore?.Dispose();
            FlushBatchAsync().GetAwaiter().GetResult();
            RetryFailedBatchesAsync().GetAwaiter().GetResult();
            _serviceBusSender?.DisposeAsync().GetAwaiter().GetResult();
            _serviceBusClient?.DisposeAsync().GetAwaiter().GetResult();

            _disposed = true;
        }
    }
}