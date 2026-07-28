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

namespace SeliseBlocks.LMT.Client;


public sealed class LmtRabbitMqSender : ILmtMessageSender
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

    private Task RetryFailedBatchesAsync() =>
        LmtFailedBatchRetryHelper.RetryFailedBatchesAsync(_retrySemaphore, RetryFailedLogsAsync, RetryFailedTracesAsync);

    private Task RetryFailedLogsAsync(DateTime now) =>
        LmtFailedBatchRetryHelper.RetryDueBatchesAsync(
            _failedLogBatches,
            now,
            _maxRetries,
            batch => batch.NextRetryTime,
            batch => batch.RetryCount,
            batch => LmtRabbitMqSenderLog.LogBatchExceededRetries(Logger, _maxRetries, batch.Logs.Count),
            batch => SendLogsAsync(batch.Logs, batch.RetryCount));

    private Task RetryFailedTracesAsync(DateTime now) =>
        LmtFailedBatchRetryHelper.RetryDueBatchesAsync(
            _failedTraceBatches,
            now,
            _maxRetries,
            batch => batch.NextRetryTime,
            batch => batch.RetryCount,
            _ => LmtRabbitMqSenderLog.TraceBatchExceededRetries(Logger, _maxRetries),
            batch => SendTracesAsync(batch.TenantBatches, batch.RetryCount));

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

internal static class LmtRabbitMqSenderLog
{
    private static readonly Action<ILogger, int, int, Exception?> SendingLogsFailedMessage =
        LoggerMessage.Define<int, int>(
            LogLevel.Warning,
            new EventId(5000),
            "Exception sending logs to RabbitMQ. Retry {CurrentRetry}/{MaxRetries}.");

    private static readonly Action<ILogger, int, Exception?> LogBatchQueueFullMessage =
        LoggerMessage.Define<int>(
            LogLevel.Warning,
            new EventId(5001),
            "Failed log batch queue is full ({MaxFailedBatches}). Dropping batch.");

    private static readonly Action<ILogger, int, int, Exception?> SendingTracesFailedMessage =
        LoggerMessage.Define<int, int>(
            LogLevel.Warning,
            new EventId(5002),
            "Exception sending traces to RabbitMQ. Retry {CurrentRetry}/{MaxRetries}.");

    private static readonly Action<ILogger, int, Exception?> TraceBatchQueueFullMessage =
        LoggerMessage.Define<int>(
            LogLevel.Warning,
            new EventId(5003),
            "Failed trace batch queue is full ({MaxFailedBatches}). Dropping batch.");

    private static readonly Action<ILogger, string, string, string, Exception?> PublishingMessageMessage =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Debug,
            new EventId(5004),
            "Publishing RabbitMQ message exchange={ExchangeName}, routingKey={RoutingKey}, messageId={MessageId}.");

    private static readonly Action<ILogger, int, int, Exception?> LogBatchExceededRetriesMessage =
        LoggerMessage.Define<int, int>(
            LogLevel.Warning,
            new EventId(5005),
            "Log batch exceeded max retries ({MaxRetries}). Dropping batch with {LogCount} logs.");

    private static readonly Action<ILogger, int, Exception?> TraceBatchExceededRetriesMessage =
        LoggerMessage.Define<int>(
            LogLevel.Warning,
            new EventId(5006),
            "Trace batch exceeded max retries ({MaxRetries}). Dropping batch.");

    public static void SendingLogsFailed(ILogger logger, Exception exception, int currentRetry, int maxRetries) =>
        SendingLogsFailedMessage(logger, currentRetry, maxRetries, exception);

    public static void LogBatchQueueFull(ILogger logger, int maxFailedBatches) =>
        LogBatchQueueFullMessage(logger, maxFailedBatches, null);

    public static void SendingTracesFailed(ILogger logger, Exception exception, int currentRetry, int maxRetries) =>
        SendingTracesFailedMessage(logger, currentRetry, maxRetries, exception);

    public static void TraceBatchQueueFull(ILogger logger, int maxFailedBatches) =>
        TraceBatchQueueFullMessage(logger, maxFailedBatches, null);

    public static void PublishingMessage(ILogger logger, string exchangeName, string routingKey, string messageId) =>
        PublishingMessageMessage(logger, exchangeName, routingKey, messageId, null);

    public static void LogBatchExceededRetries(ILogger logger, int maxRetries, int logCount) =>
        LogBatchExceededRetriesMessage(logger, maxRetries, logCount, null);

    public static void TraceBatchExceededRetries(ILogger logger, int maxRetries) =>
        TraceBatchExceededRetriesMessage(logger, maxRetries, null);
}
