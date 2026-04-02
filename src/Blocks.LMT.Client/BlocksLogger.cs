using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SeliseBlocks.LMT.Client
{
    public class BlocksLogger : IBlocksLogger
    {
        private readonly LmtOptions _options;
        private readonly ConcurrentQueue<LogData> _logBatch;
        private readonly PeriodicTimer _flushTimer;
        private readonly ILmtMessageSender _serviceBusSender;
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private readonly CancellationTokenSource _disposeCts = new();
        private readonly Task _flushLoopTask;
        private bool _disposed;

        public BlocksLogger(LmtOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            if (string.IsNullOrWhiteSpace(_options.ServiceId))
                throw new ArgumentException("ServiceName is required", nameof(options));

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
            if (args.Length == 0)
            {
                return messageTemplate;
            }

            var placeholderRegex = new Regex(@"\{(.*?)\}");
            var matches = placeholderRegex.Matches(messageTemplate);

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
                    properties[key] = args[i] ?? string.Empty;
            }

            int index = 0;
            return placeholderRegex.Replace(messageTemplate, match =>
            {
                if (index >= args.Length)
                    return match.Value;

                var value = args[index]?.ToString() ?? string.Empty;
                index++;
                return value;
            });
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
