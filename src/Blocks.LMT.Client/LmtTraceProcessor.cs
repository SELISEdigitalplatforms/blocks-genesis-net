using OpenTelemetry;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SeliseBlocks.LMT.Client
{
    public class LmtTraceProcessor : BaseProcessor<Activity>
    {
        private readonly LmtOptions _options;
        private readonly ConcurrentQueue<TraceData> _traceBatch;
        private readonly PeriodicTimer _flushTimer;
        private readonly ILmtMessageSender _serviceBusSender;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _disposeCts = new();
        private readonly Task _flushLoopTask;
        private bool _disposed;

        public LmtTraceProcessor(LmtOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            _traceBatch = new ConcurrentQueue<TraceData>();

            _serviceBusSender = LmtMessageSenderFactory.CreateShared(_options);

            var flushInterval = TimeSpan.FromSeconds(_options.FlushIntervalSeconds);
            _flushTimer = new PeriodicTimer(flushInterval);
            _flushLoopTask = RunPeriodicFlushAsync(_disposeCts.Token);
        }

        public override void OnEnd(Activity activity)
        {
            if (!_options.EnableTracing) return;

                if (_disposed) return;

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
                _ = Task.Run(FlushBatchSafeAsync);
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
                Trace.TraceError($"Error flushing traces: {ex}");
            }
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

            _disposed = true;

            if (disposing)
            {
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
                finally
                {
                    _disposeCts.Dispose();
                    _semaphore.Dispose();
                    _serviceBusSender.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }
}