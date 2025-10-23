using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions; // For parsing message templates
using Microsoft.Extensions.Logging; // For LogLevel enum mapping

namespace SeliseBlocks.LMT.Client
{
    public class BlocksLogger : IBlocksLogger
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

            if (string.IsNullOrWhiteSpace(_options.ConnectionString))
                throw new ArgumentException("ServiceBusConnectionString is required", nameof(options));

            _logBatch = new ConcurrentQueue<LogData>();

            _serviceBusSender = new LmtServiceBusSender(
                _options.ServiceId,
                _options.ConnectionString,
                _options.MaxRetries,
                _options.MaxFailedBatches);

            var flushInterval = TimeSpan.FromSeconds(_options.FlushIntervalSeconds);
            _flushTimer = new Timer(async _ => await FlushBatchAsync(), null, flushInterval, flushInterval);
        }

        public void Log(LmtLogLevel level, string messageTemplate, Exception? exception = null, params object?[] args)
        {
            if (!_options.EnableLogging) return;

            var activity = Activity.Current;
            var properties = new Dictionary<string, object>();
            string formattedMessage = FormatLogMessage(messageTemplate, args, properties);

            var logData = new LogData
            {
                Timestamp = DateTime.UtcNow,
                Level = level.ToString(),
                Message = formattedMessage,
                Exception = exception?.ToString() ?? string.Empty,
                ServiceName = _options.ServiceId,
                Properties = properties, // Use the parsed properties
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
                // Do not await to avoid blocking the caller of Log.
                // This will run on a ThreadPool thread.
                _ = Task.Run(() => FlushBatchAsync());
            }
        }

        public void LogTrace(string messageTemplate, params object?[] args)
            => Log(LmtLogLevel.Trace, messageTemplate, null, args);

        public void LogDebug(string messageTemplate, params object?[] args)
            => Log(LmtLogLevel.Debug, messageTemplate, null, args);

        public void LogInformation(string messageTemplate, params object?[] args)
            => Log(LmtLogLevel.Information, messageTemplate, null, args);

        public void LogWarning(string messageTemplate, params object?[] args)
            => Log(LmtLogLevel.Warning, messageTemplate, null, args);

        public void LogError(string messageTemplate, Exception? exception = null, params object?[] args)
            => Log(LmtLogLevel.Error, messageTemplate, exception, args);

        public void LogCritical(string messageTemplate, Exception? exception = null, params object?[] args)
            => Log(LmtLogLevel.Critical, messageTemplate, exception, args);


        private string FormatLogMessage(string messageTemplate, object?[] args, Dictionary<string, object> properties)
        {
            if (args.Length > 0)
            {
                // Add each arg as Arg0, Arg1, etc. to the properties dictionary
                for (int i = 0; i < args.Length; i++)
                {
                    if (!properties.ContainsKey($"Arg{i}"))
                        properties[$"Arg{i}"] = args[i];
                }


                var formatted = messageTemplate;
                var regex = new Regex(@"\{(.*?)\}");

                int index = 0;
                formatted = regex.Replace(formatted, match =>
                {
                    // If we run out of args, leave placeholder as-is
                    if (index >= args.Length)
                        return match.Value;

                    var value = args[index]?.ToString() ?? string.Empty;
                    index++;
                    return value;
                });

                return formatted;
            }

            return messageTemplate;
        }



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
            catch (Exception ex)
            {
                Debug.WriteLine($"Error flushing logs: {ex}");
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _flushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _flushTimer?.Dispose();

            _semaphore.Wait();
            try
            {
                FlushBatchAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during synchronous flush on dispose: {ex}");
            }
            finally
            {
                _semaphore.Release();
                _semaphore?.Dispose();
            }

            _serviceBusSender?.Dispose();

            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}