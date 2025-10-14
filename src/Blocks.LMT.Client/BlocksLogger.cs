using System.Collections.Concurrent;
using System.Diagnostics;

namespace SeliseBlocks.LMT.Client
{
    public class BlocksLogger : IBlocksLogger, IDisposable
    {
        private readonly LmtOptions _options;
        private readonly ConcurrentQueue<LogData> _logBatch;
        private readonly Timer _flushTimer;
        private readonly LmtServiceBusSender _serviceBusSender;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private bool _disposed;

        public BlocksLogger(LmtOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            if (string.IsNullOrWhiteSpace(_options.ServiceId))
                throw new ArgumentException("ServiceName is required", nameof(options));

            if (string.IsNullOrWhiteSpace(_options.ServiceBusConnectionString))
                throw new ArgumentException("ServiceBusConnectionString is required", nameof(options));

            _logBatch = new ConcurrentQueue<LogData>();

            _serviceBusSender = new LmtServiceBusSender(
                _options.ServiceId,
                _options.ServiceBusConnectionString,
                _options.MaxRetries,
                _options.MaxFailedBatches);

            var flushInterval = TimeSpan.FromSeconds(_options.FlushIntervalSeconds);
            _flushTimer = new Timer(async _ => await FlushBatchAsync(), null, flushInterval, flushInterval);
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
                ServiceName = _options.ServiceId,
                Properties = properties ?? new Dictionary<string, object>(),
                TenantId = _options.XBlocksKey
            };

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
                    await _serviceBusSender.SendLogsAsync(logs);
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _flushTimer?.Dispose();
            _semaphore?.Dispose();
            FlushBatchAsync().GetAwaiter().GetResult();
            _serviceBusSender?.Dispose();

            _disposed = true;
        }
    }
}