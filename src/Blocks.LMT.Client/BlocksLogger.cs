using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SeliseBlocks.LMT.Client
{
    /// <summary>
    /// Implementation of IBlocksLogger for Logging and Monitoring Telemetry (LMT).
    /// Batches log entries and sends them to Azure Service Bus asynchronously.
    /// </summary>
    public class BlocksLogger : IBlocksLogger
    {
        private static readonly Regex PlaceholderRegex = new(@"\{(.*?)\}", RegexOptions.Compiled);
        
        private readonly LmtOptions _options;
        private readonly ConcurrentQueue<LogData> _logBatch;
        private readonly PeriodicTimer _flushTimer;
        private readonly ILmtMessageSender _serviceBusSender;
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private readonly CancellationTokenSource _disposeCts = new();
        private readonly Task _flushLoopTask;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="BlocksLogger"/> class.
        /// </summary>
        /// <param name="options">Configuration options for the logger</param>
        /// <exception cref="ArgumentNullException">Thrown when options is null</exception>
        /// <exception cref="ArgumentException">Thrown when required options are not provided</exception>
        public BlocksLogger(LmtOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            // Validate options
            if (string.IsNullOrWhiteSpace(_options.ServiceId))
                throw new ArgumentException("ServiceId is required", nameof(options));

            if (string.IsNullOrWhiteSpace(_options.ConnectionString))
                throw new ArgumentException("ConnectionString is required", nameof(options));

            _logBatch = new ConcurrentQueue<LogData>();
            _serviceBusSender = LmtMessageSenderFactory.CreateShared(_options);

            var flushInterval = TimeSpan.FromSeconds(_options.FlushIntervalSeconds);
            _flushTimer = new PeriodicTimer(flushInterval);
            _flushLoopTask = RunPeriodicFlushAsync(_disposeCts.Token);
        }

        public void Log(LmtLogLevel level, string messageTemplate, Exception? exception = null, params object?[] args)
        {
            if (!_options.EnableLogging || _disposed)
            {
                return;
            }

            // Validate and sanitize messageTemplate
            if (string.IsNullOrWhiteSpace(messageTemplate))
            {
                messageTemplate = "<empty message>";
            }

            var activity = Activity.Current;
            var properties = new Dictionary<string, object>();
            string formattedMessage = FormatLogMessage(messageTemplate, args, properties);

            var logData = new LogData
            {
                Timestamp = DateTime.UtcNow,
                Level = level.ToString(),
                Message = formattedMessage,
                Exception = SanitizeException(exception),
                ServiceName = _options.ServiceId,
                Properties = properties,
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
                _ = Task.Run(FlushBatchSafeAsync);
            }
        }

        private static string SanitizeException(Exception? exception)
        {
            if (exception == null)
                return string.Empty;

            // Only include exception type and message, not full stack trace
            return $"{exception.GetType().Name}: {exception.Message}";
        }

        /// <summary>
        /// Logs a message at trace level.
        /// </summary>
        /// <param name="messageTemplate">The message template</param>
        /// <param name="args">Optional format arguments</param>
        public void LogTrace(string messageTemplate, params object?[] args)
            => Log(LmtLogLevel.Trace, messageTemplate, null, args);

        /// <summary>
        /// Logs a message at debug level.
        /// </summary>
        /// <param name="messageTemplate">The message template</param>
        /// <param name="args">Optional format arguments</param>
        public void LogDebug(string messageTemplate, params object?[] args)
            => Log(LmtLogLevel.Debug, messageTemplate, null, args);

        /// <summary>
        /// Logs a message at information level.
        /// </summary>
        /// <param name="messageTemplate">The message template</param>
        /// <param name="args">Optional format arguments</param>
        public void LogInformation(string messageTemplate, params object?[] args)
            => Log(LmtLogLevel.Information, messageTemplate, null, args);

        /// <summary>
        /// Logs a message at warning level.
        /// </summary>
        /// <param name="messageTemplate">The message template</param>
        /// <param name="args">Optional format arguments</param>
        public void LogWarning(string messageTemplate, params object?[] args)
            => Log(LmtLogLevel.Warning, messageTemplate, null, args);

        /// <summary>
        /// Logs a message at error level with optional exception.
        /// </summary>
        /// <param name="messageTemplate">The message template</param>
        /// <param name="exception">Optional exception to log</param>
        /// <param name="args">Optional format arguments</param>
        public void LogError(string messageTemplate, Exception? exception = null, params object?[] args)
            => Log(LmtLogLevel.Error, messageTemplate, exception, args);

        /// <summary>
        /// Logs a message at critical level with optional exception.
        /// </summary>
        /// <param name="messageTemplate">The message template</param>
        /// <param name="exception">Optional exception to log</param>
        /// <param name="args">Optional format arguments</param>
        public void LogCritical(string messageTemplate, Exception? exception = null, params object?[] args)
            => Log(LmtLogLevel.Critical, messageTemplate, exception, args);

        private string FormatLogMessage(string messageTemplate, object?[] args, Dictionary<string, object> properties)
        {
            if (args.Length == 0)
            {
                return messageTemplate;
            }

            var matches = PlaceholderRegex.Matches(messageTemplate);

            for (int i = 0; i < args.Length; i++)
            {
                var key = $"Arg{i}";
                if (i < matches.Count)
                {
                    var nameMatch = Regex.Match(matches[i].Groups[1].Value, @"^@?(\w+)");
                    if (nameMatch.Success)
                        key = nameMatch.Groups[1].Value;
                }

                if (!properties.ContainsKey(key))
                {
                    // Sanitize object to prevent exposing sensitive data
                    var sanitizedValue = SanitizeArgument(args[i]);
                    properties[key] = sanitizedValue;
                }
            }

            int index = 0;
            return PlaceholderRegex.Replace(messageTemplate, match =>
            {
                if (index >= args.Length)
                    return match.Value;

                var value = args[index]?.ToString() ?? string.Empty;
                index++;
                return value;
            });
        }

        private static object SanitizeArgument(object? arg)
        {
            if (arg == null)
                return string.Empty;

            var type = arg.GetType();

            // Don't log exceptions directly - they'll include stack traces
            if (arg is Exception ex)
                return ex.GetType().Name; // Only log exception type, not stack trace

            // String arguments are safe
            if (arg is string)
                return arg;

            // For other types, convert to string (safe)
            return arg;
        }

        private async Task RunPeriodicFlushAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (await _flushTimer.WaitForNextTickAsync(cancellationToken))
                {
                    await FlushBatchSafeAsync();
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task FlushBatchSafeAsync()
        {
            try
            {
                await FlushBatchAsync();
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Error flushing logs: {ex}");
            }
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
                Trace.TraceError($"Error flushing logs: {ex}");
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _disposeCts.Cancel();
            _flushTimer.Dispose();

            try
            {
                _flushLoopTask.GetAwaiter().GetResult();
                FlushBatchAsync().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Error during logger dispose flush: {ex}");
            }
            finally
            {
                _disposeCts.Dispose();
                _semaphore.Dispose();
                _serviceBusSender.Dispose();
                GC.SuppressFinalize(this);
            }
        }
    }
}
