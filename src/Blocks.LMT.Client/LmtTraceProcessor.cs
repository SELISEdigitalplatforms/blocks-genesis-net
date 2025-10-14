using OpenTelemetry;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SeliseBlocks.LMT.Client
{
    public class LmtTraceProcessor : BaseProcessor<Activity>
    {
        private readonly LmtOptions _options;
        private readonly ConcurrentQueue<TraceData> _traceBatch;
        private readonly Timer _flushTimer;
        private readonly LmtServiceBusSender _serviceBusSender;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private bool _disposed;

        public LmtTraceProcessor(LmtOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            _traceBatch = new ConcurrentQueue<TraceData>();

            // Use shared sender
            _serviceBusSender = new LmtServiceBusSender(
                _options.ServiceId,
                _options.ServiceBusConnectionString,
                _options.MaxRetries,
                _options.MaxFailedBatches);

            var flushInterval = TimeSpan.FromSeconds(_options.FlushIntervalSeconds);
            _flushTimer = new Timer(async _ => await FlushBatchAsync(), null, flushInterval, flushInterval);
        }

        public override void OnEnd(Activity activity)
        {
            if (!_options.EnableTracing) return;

            var endTime = activity.StartTimeUtc.Add(activity.Duration);

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
                ServiceName = _options.ServiceId,
                TenantId = _options.XBlocksKey
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
                    await _serviceBusSender.SendTracesAsync(tenantBatches);
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                _flushTimer?.Dispose();
                _semaphore?.Dispose();
                FlushBatchAsync().GetAwaiter().GetResult();
                _serviceBusSender?.Dispose();
            }

            _disposed = true;
            base.Dispose(disposing);
        }
    }
}