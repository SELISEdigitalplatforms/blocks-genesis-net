using Azure.Messaging.ServiceBus;
using OpenTelemetry;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Blocks.LMT.Client
{
    public class LmtTraceProcessor : BaseProcessor<Activity>
    {
        private readonly LmtOptions _options;
        private readonly ConcurrentQueue<TraceData> _traceBatch;
        private readonly ConcurrentQueue<FailedTraceBatch> _failedBatches;
        private readonly Timer _flushTimer;
        private readonly Timer _retryTimer;
        private ServiceBusClient? _serviceBusClient;
        private ServiceBusSender? _serviceBusSender;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _retrySemaphore = new SemaphoreSlim(1, 1);
        private bool _disposed;

        public LmtTraceProcessor(LmtOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            _traceBatch = new ConcurrentQueue<TraceData>();
            _failedBatches = new ConcurrentQueue<FailedTraceBatch>();

            // Initialize Service Bus
            _serviceBusClient = new ServiceBusClient(_options.TracesServiceBusConnectionString);
            _serviceBusSender = _serviceBusClient.CreateSender("blocks-lmt-sevice-traces");

            var flushInterval = TimeSpan.FromSeconds(_options.FlushIntervalSeconds);
            _flushTimer = new Timer(async _ => await FlushBatchAsync(), null, flushInterval, flushInterval);
            _retryTimer = new Timer(async _ => await RetryFailedBatchesAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        public override void OnEnd(Activity activity)
        {
            if (!_options.EnableTracing) return;

            var endTime = activity.StartTimeUtc.Add(activity.Duration);
            var tenantId = Baggage.GetBaggage("TenantId") ?? "Miscellaneous";

            var traceData = new TraceData
            {
                Timestamp = endTime,
                TraceId = activity.TraceId.ToString(),
                SpanId = activity.SpanId.ToString(),
                ParentSpanId = activity.ParentSpanId.ToString(),
                ParentId = activity.ParentId?.ToString() ?? string.Empty,
                Kind = activity.Kind.ToString(),
                ActivitySourceName = activity.Source.Name,
                OperationName = activity.DisplayName,
                StartTime = activity.StartTimeUtc,
                EndTime = endTime,
                Duration = activity.Duration.TotalMilliseconds,
                Attributes = activity.TagObjects?.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value
                ) ?? new Dictionary<string, object?>(),
                Status = activity.Status.ToString(),
                StatusDescription = activity.StatusDescription ?? string.Empty,
                Baggage = GetBaggageItems(),
                ServiceName = _options.ServiceName,
                TenantId = tenantId
            };

            _traceBatch.Enqueue(traceData);

            if (_traceBatch.Count >= _options.TraceBatchSize)
            {
                Task.Run(() => FlushBatchAsync());
            }
        }

        private static Dictionary<string, string> GetBaggageItems()
        {
            var baggage = new Dictionary<string, string>();
            foreach (var item in Baggage.Current)
            {
                baggage[item.Key] = item.Value;
            }
            return baggage;
        }

        private async Task FlushBatchAsync()
        {
            await _semaphore.WaitAsync();
            try
            {
                var tenantBatches = new Dictionary<string, List<TraceData>>();

                while (_traceBatch.TryDequeue(out var trace))
                {
                    if (!tenantBatches.ContainsKey(trace.TenantId))
                    {
                        tenantBatches[trace.TenantId] = new List<TraceData>();
                    }
                    tenantBatches[trace.TenantId].Add(trace);
                }

                if (tenantBatches.Count > 0)
                {
                    await SendToServiceBusAsync(tenantBatches);
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private async Task SendToServiceBusAsync(Dictionary<string, List<TraceData>> tenantBatches, int retryCount = 0)
        {
            int currentRetry = 0;

            while (currentRetry <= _options.MaxRetries)
            {
                try
                {
                    var payload = new
                    {
                        Type = "traces",
                        ServiceName = _options.ServiceName,
                        Data = tenantBatches
                    };

                    var json = JsonSerializer.Serialize(payload);
                    var timestamp = DateTime.UtcNow;
                    var messageId = $"traces_{_options.ServiceName}_{timestamp:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}";

                    var message = new ServiceBusMessage(json)
                    {
                        ContentType = "application/json",
                        MessageId = messageId,
                        CorrelationId = _options.ServiceName,
                        ApplicationProperties =
                        {
                            { "serviceName", _options.ServiceName },
                            { "timestamp", timestamp.ToString("o") },
                            { "source", "TracesClient" },
                            { "type", "traces" }
                        }
                    };

                    await _serviceBusSender!.SendMessageAsync(message);
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Exception sending traces to Service Bus: {ex.Message}");
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
                var failedBatch = new FailedTraceBatch
                {
                    TenantBatches = tenantBatches,
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
                var batchesToRetry = new List<FailedTraceBatch>();
                var batchesToRequeue = new List<FailedTraceBatch>();

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

                    await SendToServiceBusAsync(failedBatch.TenantBatches, failedBatch.RetryCount);
                }
            }
            finally
            {
                _retrySemaphore.Release();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                _flushTimer?.Dispose();
                _retryTimer?.Dispose();
                _semaphore?.Dispose();
                _retrySemaphore?.Dispose();
                FlushBatchAsync().GetAwaiter().GetResult();
                RetryFailedBatchesAsync().GetAwaiter().GetResult();
                _serviceBusSender?.DisposeAsync().GetAwaiter().GetResult();
                _serviceBusClient?.DisposeAsync().GetAwaiter().GetResult();
            }

            _disposed = true;
            base.Dispose(disposing);
        }
    }
}