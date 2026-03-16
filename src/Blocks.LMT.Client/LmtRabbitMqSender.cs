using RabbitMQ.Client;
using SeliseBlocks.LMT.Client;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Blocks.LMT.Client
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

        public LmtRabbitMqSender(
            string serviceName,
            string rabbitMqConnectionString,
            int maxRetries = 3,
            int maxFailedBatches = 100)
        {
            _serviceName = serviceName;
            _maxRetries = maxRetries;
            _maxFailedBatches = maxFailedBatches;

            _factory = new ConnectionFactory
            {
                Uri = new Uri(rabbitMqConnectionString),
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(10),
                ClientProvidedName = $"seliseblocks-lmt-client-{serviceName}"
            };

            _retryTimer = new Timer(async _ => await RetryFailedBatchesAsync(), null,
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        public async Task SendLogsAsync(List<LogData> logs, int retryCount = 0)
        {
            int currentRetry = 0;

            while (currentRetry <= _maxRetries)
            {
                try
                {
                    await EnsureChannelAsync();

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
                        type: "logs");

                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Exception sending logs to RabbitMQ: {ex.Message}, Retry: {currentRetry}/{_maxRetries}");
                }

                currentRetry++;

                if (currentRetry <= _maxRetries)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, currentRetry - 1));
                    await Task.Delay(delay);
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
                Console.WriteLine($"Failed log batch queue is full ({_maxFailedBatches}). Dropping batch.");
            }
        }

        public async Task SendTracesAsync(Dictionary<string, List<TraceData>> tenantBatches, int retryCount = 0)
        {
            int currentRetry = 0;

            while (currentRetry <= _maxRetries)
            {
                try
                {
                    await EnsureChannelAsync();

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
                        type: "traces");

                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Exception sending traces to RabbitMQ: {ex.Message}, Retry: {currentRetry}/{_maxRetries}");
                }

                currentRetry++;

                if (currentRetry <= _maxRetries)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, currentRetry - 1));
                    await Task.Delay(delay);
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
                Console.WriteLine($"Failed trace batch queue is full ({_maxFailedBatches}). Dropping batch.");
            }
        }

        private async Task EnsureChannelAsync()
        {
            if (_connection is { IsOpen: true } && _channel is { IsOpen: true })
                return;

            _connection?.Dispose();
            _connection = await _factory.CreateConnectionAsync();

            _channel?.Dispose();
            _channel = await _connection.CreateChannelAsync();

            var exchangeName = LmtConstants.GetRabbitMqExchangeName(_serviceName);

            await _channel.ExchangeDeclareAsync(
                exchange: exchangeName,
                type: ExchangeType.Direct,
                durable: true,
                autoDelete: false);
        }

        private async Task PublishAsync(string routingKey, object payload, string source, string type)
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

            await _channel.BasicPublishAsync(
                exchange: exchangeName,
                routingKey: routingKey,
                mandatory: false,
                basicProperties: properties,
                body: body);
        }

        private async Task RetryFailedBatchesAsync()
        {
            if (!await _retrySemaphore.WaitAsync(0))
                return;

            try
            {
                var now = DateTime.UtcNow;
                await RetryFailedLogsAsync(now);
                await RetryFailedTracesAsync(now);
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
                    Console.WriteLine($"Log batch exceeded max retries ({_maxRetries}). Dropping batch with {failedBatch.Logs.Count} logs.");
                    continue;
                }

                await SendLogsAsync(failedBatch.Logs, failedBatch.RetryCount);
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
                    Console.WriteLine($"Trace batch exceeded max retries ({_maxRetries}). Dropping batch.");
                    continue;
                }

                await SendTracesAsync(failedBatch.TenantBatches, failedBatch.RetryCount);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _retryTimer.Dispose();
            _retrySemaphore.Dispose();
            RetryFailedBatchesAsync().GetAwaiter().GetResult();
            _channel?.Dispose();
            _connection?.Dispose();

            _disposed = true;
        }
    }
    
}
