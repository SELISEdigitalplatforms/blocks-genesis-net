using Azure.Messaging.ServiceBus;
using System.Collections.Concurrent;
using System.Diagnostics;
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
        private readonly PeriodicTimer _retryTimer;
        private ServiceBusClient? _serviceBusClient;
        private ServiceBusSender? _serviceBusSender;
        private readonly SemaphoreSlim _retrySemaphore = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _disposeCts = new();
        private readonly Task _retryLoopTask;
        private bool _disposed;

        public LmtServiceBusSender(
            string serviceName,
            string serviceBusConnectionString,
            int maxRetries = 3,
            int maxFailedBatches = 100)
        {
            _serviceName = serviceName;
            _maxRetries = maxRetries;
            _maxFailedBatches = maxFailedBatches;

            _failedLogBatches = new ConcurrentQueue<FailedLogBatch>();
            _failedTraceBatches = new ConcurrentQueue<FailedTraceBatch>();

            if (!string.IsNullOrWhiteSpace(serviceBusConnectionString))
            {
                _serviceBusClient = new ServiceBusClient(serviceBusConnectionString);
                _serviceBusSender = _serviceBusClient.CreateSender(LmtConstants.GetTopicName(serviceName));
            }

            _retryTimer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            _retryLoopTask = RunRetryLoopAsync(_disposeCts.Token);
        }

        public async Task SendLogsAsync(List<LogData> logs, int retryCount = 0)
        {
            if (_serviceBusSender == null)
            {
                Trace.TraceWarning("Service Bus sender not initialized");
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

                    await _serviceBusSender.SendMessageAsync(message);
                    return;
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"Exception sending logs to Service Bus. Retry {currentRetry}/{_maxRetries}: {ex}");
                }

                currentRetry++;

                if (currentRetry <= _maxRetries)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, currentRetry - 1));
                    await Task.Delay(delay);
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
                Trace.TraceWarning($"Queued log batch for later retry. Failed batches in queue: {_failedLogBatches.Count}");
            }
            else
            {
                Trace.TraceWarning($"Failed log batch queue is full ({_maxFailedBatches}). Dropping batch.");
            }
        }

        public async Task SendTracesAsync(Dictionary<string, List<TraceData>> tenantBatches, int retryCount = 0)
        {
            if (_serviceBusSender == null)
            {
                Trace.TraceWarning("Service Bus sender not initialized");
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

                    await _serviceBusSender.SendMessageAsync(message);
                    return;
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"Exception sending traces to Service Bus. Retry {currentRetry}/{_maxRetries}: {ex}");
                }

                currentRetry++;

                if (currentRetry <= _maxRetries)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, currentRetry - 1));
                    await Task.Delay(delay);
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
                Trace.TraceWarning($"Queued trace batch for later retry. Failed batches in queue: {_failedTraceBatches.Count}");
            }
            else
            {
                Trace.TraceWarning($"Failed trace batch queue is full ({_maxFailedBatches}). Dropping batch.");
            }
        }

        private async Task RunRetryLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (await _retryTimer.WaitForNextTickAsync(cancellationToken))
                {
                    await RetryFailedBatchesAsync();
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task RetryFailedBatchesAsync()
        {
            if (!await _retrySemaphore.WaitAsync(0))
                return;

            try
            {
                var now = DateTime.UtcNow;

                // Retry failed logs
                await RetryFailedLogsAsync(now);

                // Retry failed traces
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
            {
                _failedLogBatches.Enqueue(batch);
            }

            foreach (var failedBatch in batchesToRetry)
            {
                if (failedBatch.RetryCount >= _maxRetries)
                {
                    Trace.TraceWarning($"Log batch exceeded max retries ({_maxRetries}). Dropping batch with {failedBatch.Logs.Count} logs.");
                    continue;
                }

                Trace.TraceInformation($"Retrying failed log batch (Attempt {failedBatch.RetryCount + 1}/{_maxRetries})");
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
            {
                _failedTraceBatches.Enqueue(batch);
            }

            foreach (var failedBatch in batchesToRetry)
            {
                if (failedBatch.RetryCount >= _maxRetries)
                {
                    Trace.TraceWarning($"Trace batch exceeded max retries ({_maxRetries}). Dropping batch.");
                    continue;
                }

                Trace.TraceInformation($"Retrying failed trace batch (Attempt {failedBatch.RetryCount + 1}/{_maxRetries})");
                await SendTracesAsync(failedBatch.TenantBatches, failedBatch.RetryCount);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            _disposeCts.Cancel();
            _retryTimer.Dispose();

            try
            {
                _retryLoopTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _disposeCts.Dispose();
                _retrySemaphore.Dispose();
                _serviceBusSender?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _serviceBusClient?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }
}