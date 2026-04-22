using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using SeliseBlocks.LMT.Client;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SeliseBlocks.LMT.Client
{
   
    public class LmtRabbitMqSender : ILmtMessageSender
    {
        private readonly string _serviceName;
        private readonly int _maxRetries;
        private readonly int _maxFailedBatches;

        private readonly ConcurrentQueue<FailedLogBatch> _failedLogBatches = new();
        private readonly ConcurrentQueue<FailedTraceBatch> _failedTraceBatches = new();
        private readonly SemaphoreSlim _retrySemaphore = new(1, 1);
        private readonly Timer _retryTimer;

        private readonly ConnectionFactory _factory;
        private IConnection? _connection;
        private IChannel? _channel;
        private bool _disposed;
        private readonly SemaphoreSlim _publishSemaphore = new(1, 1);
        private readonly ILogger<LmtRabbitMqSender>? _logger;
        private ILogger Logger => _logger ?? NullLogger<LmtRabbitMqSender>.Instance;

        public LmtRabbitMqSender(
            string serviceName,
            string rabbitMqConnectionString,
            int maxRetries = 3,
            int maxFailedBatches = 100,
            ILogger<LmtRabbitMqSender>? logger = null)
        {
            _serviceName = serviceName;
            _maxRetries = maxRetries;
            _maxFailedBatches = maxFailedBatches;
            _logger = logger ?? NullLogger<LmtRabbitMqSender>.Instance;

            _factory = new ConnectionFactory
            {
                Uri = new Uri(rabbitMqConnectionString),
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(10),
                ClientProvidedName = $"seliseblocks-lmt-client-{serviceName}"
            };

            _retryTimer = new Timer(async _ => await RetryFailedBatchesAsync().ConfigureAwait(false), null,
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        public async Task SendLogsAsync(List<LogData> logs, int retryCount = 0)
        {
            int currentRetry = 0;

            while (currentRetry <= _maxRetries)
            {
                try
                {
                   await EnsureChannelAsync().ConfigureAwait(false);

                    var payload = new
                    {
                        Type = "logs",
                        ServiceName = _serviceName,
                        Data = logs
                    };

                    await PublishAsync(
                        routingKey: LmtConstants.RabbitMqLogsRoutingKey,
                        payload: payload,
                        source: "LogsSender",
                        type: "logs").ConfigureAwait(false);

                    return;
                }
                catch (Exception ex)
                {
                    LmtRabbitMqSenderLog.SendingLogsFailed(Logger, ex, currentRetry, _maxRetries);
                }

                currentRetry++;

                if (currentRetry <= _maxRetries)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, currentRetry - 1));
                    await Task.Delay(delay).ConfigureAwait(false);
                }
            }

            if (_failedLogBatches.Count < _maxFailedBatches)
            {
                _failedLogBatches.Enqueue(new FailedLogBatch
                {
                    Logs = logs,
                    RetryCount = retryCount + 1,
                    NextRetryTime = DateTime.UtcNow.AddMinutes(Math.Pow(2, retryCount))
                });
            }
            else
            {
                LmtRabbitMqSenderLog.LogBatchQueueFull(Logger, _maxFailedBatches);
            }
        }

        public async Task SendTracesAsync(Dictionary<string, List<TraceData>> tenantBatches, int retryCount = 0)
        {
            int currentRetry = 0;

            while (currentRetry <= _maxRetries)
            {
                try
                {
                    await EnsureChannelAsync().ConfigureAwait(false);

                    var payload = new
                    {
                        Type = "traces",
                        ServiceName = _serviceName,
                        Data = tenantBatches
                    };

                    await PublishAsync(
                        routingKey: LmtConstants.RabbitMqTracesRoutingKey,
                        payload: payload,
                        source: "TracesSender",
                        type: "traces").ConfigureAwait(false);

                    return;
                }
                catch (Exception ex)
                {
                    LmtRabbitMqSenderLog.SendingTracesFailed(Logger, ex, currentRetry, _maxRetries);
                }

                currentRetry++;

                if (currentRetry <= _maxRetries)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, currentRetry - 1));
                    await Task.Delay(delay).ConfigureAwait(false);
                }
            }

            if (_failedTraceBatches.Count < _maxFailedBatches)
            {
                _failedTraceBatches.Enqueue(new FailedTraceBatch
                {
                    TenantBatches = tenantBatches,
                    RetryCount = retryCount + 1,
                    NextRetryTime = DateTime.UtcNow.AddMinutes(Math.Pow(2, retryCount))
                });
            }
            else
            {
                LmtRabbitMqSenderLog.TraceBatchQueueFull(Logger, _maxFailedBatches);
            }
        }

        private async Task EnsureChannelAsync()
        {
            if (_connection is { IsOpen: true } && _channel is { IsOpen: true })
                return;

            _connection?.Dispose();
            _connection = await _factory.CreateConnectionAsync().ConfigureAwait(false);

            _channel?.Dispose();
            _channel = await _connection.CreateChannelAsync().ConfigureAwait(false);

            var exchangeName = LmtConstants.GetRabbitMqExchangeName(_serviceName);

            await _channel.ExchangeDeclareAsync(
                exchange: exchangeName,
                type: ExchangeType.Direct,
                durable: true,
                autoDelete: false).ConfigureAwait(false);
        }

        private async Task PublishAsync(string routingKey, object payload, string source, string type)
        {
            await _publishSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_channel == null)
                    throw new InvalidOperationException("RabbitMQ channel is not initialized.");

                var exchangeName = LmtConstants.GetRabbitMqExchangeName(_serviceName);
                var timestamp = DateTime.UtcNow;
                var messageId = $"{type}_{_serviceName}_{timestamp:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}";

                var json = JsonSerializer.Serialize(payload);
                var body = Encoding.UTF8.GetBytes(json);

                var properties = new BasicProperties
                {
                    ContentType = "application/json",
                    MessageId = messageId,
                    CorrelationId = type == "logs"
                        ? LmtConstants.LogSubscription
                        : LmtConstants.TraceSubscription,
                    Type = type,
                    Headers = new Dictionary<string, object?>
                    {
                        ["serviceName"] = _serviceName,
                        ["timestamp"] = timestamp.ToString("o"),
                        ["source"] = source,
                        ["type"] = type
                    }
                };

                LmtRabbitMqSenderLog.PublishingMessage(Logger, exchangeName, routingKey, messageId);

                await _channel.BasicPublishAsync(
                    exchange: exchangeName,
                    routingKey: routingKey,
                    mandatory: true,
                    basicProperties: properties,
                    body: body).ConfigureAwait(false);
            }
            finally
            {
                _publishSemaphore.Release();
            }
        }

        private async Task RetryFailedBatchesAsync()
        {
            if (!await _retrySemaphore.WaitAsync(0).ConfigureAwait(false))
                return;

            try
            {
                var now = DateTime.UtcNow;
                await RetryFailedLogsAsync(now).ConfigureAwait(false);
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
                _failedLogBatches.Enqueue(batch);

            foreach (var failedBatch in batchesToRetry)
            {
                if (failedBatch.RetryCount >= _maxRetries)
                {
                    LmtRabbitMqSenderLog.LogBatchExceededRetries(Logger, _maxRetries, failedBatch.Logs.Count);
                    continue;
                }

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
                _failedTraceBatches.Enqueue(batch);

            foreach (var failedBatch in batchesToRetry)
            {
                if (failedBatch.RetryCount >= _maxRetries)
                {
                    LmtRabbitMqSenderLog.TraceBatchExceededRetries(Logger, _maxRetries);
                    continue;
                }

                await SendTracesAsync(failedBatch.TenantBatches, failedBatch.RetryCount).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _retryTimer.Dispose();
            RetryFailedBatchesAsync().GetAwaiter().GetResult();
            _retrySemaphore.Dispose();
            _channel?.Dispose();
            _connection?.Dispose();

            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    internal static partial class LmtRabbitMqSenderLog
    {
        [LoggerMessage(EventId = 5000, Level = LogLevel.Warning, Message = "Exception sending logs to RabbitMQ. Retry {CurrentRetry}/{MaxRetries}.")]
        public static partial void SendingLogsFailed(ILogger logger, Exception exception, int currentRetry, int maxRetries);

        [LoggerMessage(EventId = 5001, Level = LogLevel.Warning, Message = "Failed log batch queue is full ({MaxFailedBatches}). Dropping batch.")]
        public static partial void LogBatchQueueFull(ILogger logger, int maxFailedBatches);

        [LoggerMessage(EventId = 5002, Level = LogLevel.Warning, Message = "Exception sending traces to RabbitMQ. Retry {CurrentRetry}/{MaxRetries}.")]
        public static partial void SendingTracesFailed(ILogger logger, Exception exception, int currentRetry, int maxRetries);

        [LoggerMessage(EventId = 5003, Level = LogLevel.Warning, Message = "Failed trace batch queue is full ({MaxFailedBatches}). Dropping batch.")]
        public static partial void TraceBatchQueueFull(ILogger logger, int maxFailedBatches);

        [LoggerMessage(EventId = 5004, Level = LogLevel.Debug, Message = "Publishing RabbitMQ message exchange={ExchangeName}, routingKey={RoutingKey}, messageId={MessageId}.")]
        public static partial void PublishingMessage(ILogger logger, string exchangeName, string routingKey, string messageId);

        [LoggerMessage(EventId = 5005, Level = LogLevel.Warning, Message = "Log batch exceeded max retries ({MaxRetries}). Dropping batch with {LogCount} logs.")]
        public static partial void LogBatchExceededRetries(ILogger logger, int maxRetries, int logCount);

        [LoggerMessage(EventId = 5006, Level = LogLevel.Warning, Message = "Trace batch exceeded max retries ({MaxRetries}). Dropping batch.")]
        public static partial void TraceBatchExceededRetries(ILogger logger, int maxRetries);
    }
}
