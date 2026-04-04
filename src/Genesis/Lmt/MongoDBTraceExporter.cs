using MongoDB.Bson;
using MongoDB.Driver;
using OpenTelemetry;
using SeliseBlocks.LMT.Client;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Blocks.Genesis
{
    public class MongoDBTraceExporter : BaseProcessor<Activity>
    {
        private readonly string _serviceName;
        private readonly ConcurrentQueue<TraceData> _batch;
        private readonly Timer _timer;
        private readonly IMongoDatabase? _database;
        private readonly int _batchSize;
        private readonly int _maxQueueSize;
        private readonly IBlocksSecret? _blocksSecret;
        private readonly ITraceCollectionEnsurer? _ensurer;
        private ILmtMessageSender? _messageSender;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private int _flushScheduled;
        private bool _disposed;

        private const int MaxParallelTenantWrites = 8;

        public MongoDBTraceExporter(
            string serviceName,
            int batchSize = 1000,
            int maxQueueSize = 100,
            IBlocksSecret? blocksSecret = null,
            ITraceCollectionEnsurer? ensurer = null)
        {
            _serviceName = serviceName;
            _maxQueueSize = Math.Clamp(maxQueueSize, 1, 100);
            _batchSize = Math.Clamp(batchSize, 1, _maxQueueSize);
            _blocksSecret = blocksSecret;
            _ensurer = ensurer;

            var interval = TimeSpan.FromSeconds(3);
            _batch = new ConcurrentQueue<TraceData>();

            var connectionString = blocksSecret?.TraceConnectionString ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                _database = LmtConfiguration.GetMongoDatabase(connectionString, LmtConfiguration.TraceDatabaseName);
            }

            _timer = new Timer(_ => TryScheduleFlush(), null, interval, interval);
        }

        private ILmtMessageSender? GetOrCreateMessageSender()
        {
            if (_messageSender != null)
                return _messageSender;

            var connectionString = LmtConfigurationProvider.GetLmtConnectionString();
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                connectionString = _blocksSecret?.LmtMessageConnectionString;
            }

            if (string.IsNullOrWhiteSpace(connectionString))
                return null;

            var maxRetries = LmtConfigurationProvider.GetLmtMaxRetries();
            var maxFailedBatches = LmtConfigurationProvider.GetLmtMaxFailedBatches();

            _messageSender = LmtMessageSenderFactory.CreateShared(new LmtOptions
            {
                ServiceId = _serviceName,
                ConnectionString = connectionString,
                MaxRetries = maxRetries,
                MaxFailedBatches = maxFailedBatches,
            });

            return _messageSender;
        }

        public override void OnEnd(Activity data)
        {
            var endTime = data.StartTimeUtc.Add(data.Duration);
            var tenantId = Baggage.GetBaggage("TenantId");
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                return;
            }

            // Apply cooperative backpressure under pressure instead of dropping data.
            while (_batch.Count >= _maxQueueSize)
            {
                TryScheduleFlush();
                Thread.Yield();
            }

            var traceData = new TraceData
            {
                Timestamp = endTime,
                TraceId = data.TraceId.ToString(),
                SpanId = data.SpanId.ToString(),
                ParentSpanId = data.ParentSpanId.ToString(),
                ParentId = data.ParentId?.ToString() ?? string.Empty,
                Kind = data.Kind.ToString(),
                ActivitySourceName = data.Source.Name,
                OperationName = data.DisplayName,
                StartTime = data.StartTimeUtc,
                EndTime = endTime,
                Duration = data.Duration.TotalMilliseconds,
                Attributes = data.TagObjects?.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value
                ) ?? new Dictionary<string, object?>(),
                Status = data.Status.ToString(),
                StatusDescription = data.StatusDescription ?? string.Empty,
                Baggage = GetBaggageItems(),
                ServiceName = _serviceName,
                TenantId = tenantId
            };

            _batch.Enqueue(traceData);

            if (_batch.Count >= _batchSize || _batch.Count >= (_maxQueueSize * 0.8))
            {
                TryScheduleFlush();
            }
        }

        private void TryScheduleFlush()
        {
            if (Interlocked.Exchange(ref _flushScheduled, 1) == 1)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await FlushBatchAsync();
                }
                finally
                {
                    Interlocked.Exchange(ref _flushScheduled, 0);

                    // If more data accumulated while flushing, schedule again.
                    if (_batch.Count >= _batchSize)
                    {
                        TryScheduleFlush();
                    }
                }
            });
        }

        private static Dictionary<string, string> GetBaggageItems()
        {
            var baggage = new Dictionary<string, string>();
            foreach (var baggageItem in Baggage.Current)
            {
                baggage[baggageItem.Key] = baggageItem.Value;
            }
            return baggage;
        }

        private async Task FlushBatchAsync()
        {
            await _semaphore.WaitAsync();
            try
            {
                var tenantBatches = new Dictionary<string, List<TraceData>>();

                while (_batch.TryDequeue(out var traceData))
                {
                    if (string.IsNullOrWhiteSpace(traceData.TenantId))
                    {
                        continue;
                    }

                    if (!tenantBatches.ContainsKey(traceData.TenantId))
                    {
                        tenantBatches[traceData.TenantId] = [];
                    }
                    tenantBatches[traceData.TenantId].Add(traceData);
                }

                if (tenantBatches.Count == 0)
                    return;

                var messageSender = GetOrCreateMessageSender();
                if (messageSender != null)
                {
                    await messageSender.SendTracesAsync(tenantBatches);
                    return;
                }

                if (_database != null)
                {
                    await SaveToMongoDBAsync(tenantBatches);
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task SaveToMongoDBAsync(Dictionary<string, List<TraceData>> tenantBatches)
        {
            using var concurrencyGate = new SemaphoreSlim(MaxParallelTenantWrites);
            var insertTasks = new List<Task>();

            foreach (var tenantBatch in tenantBatches)
            {
                if (string.IsNullOrWhiteSpace(tenantBatch.Key))
                {
                    continue;
                }

                var collectionName = tenantBatch.Key;

                // Bound per-flush parallelism to avoid resource spikes.
                var insertTask = InsertTenantBatchWithGateAsync(concurrencyGate, collectionName, tenantBatch.Value);
                insertTasks.Add(insertTask);
            }

            // Wait for all tenant inserts to complete in parallel
            if (insertTasks.Count > 0)
            {
                await Task.WhenAll(insertTasks);
            }
        }

        private async Task InsertTenantBatchWithGateAsync(SemaphoreSlim concurrencyGate, string collectionName, List<TraceData> traceDataList)
        {
            await concurrencyGate.WaitAsync();
            try
            {
                await InsertTenantBatchAsync(collectionName, traceDataList);
            }
            finally
            {
                concurrencyGate.Release();
            }
        }

        private async Task InsertTenantBatchAsync(string collectionName, List<TraceData> traceDataList)
        {
            try
            {
                // Ensure collection exists and all caches updated (handled by ensurer)
                if (_ensurer != null)
                {
                    await _ensurer.EnsureAndCacheAsync(collectionName);
                }

                var collection = _database!.GetCollection<BsonDocument>(collectionName);
                var bsonDocuments = traceDataList.Select(ConvertToBsonDocument).ToList();
                await collection.InsertManyAsync(bsonDocuments);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to insert batch for tenant '{collectionName}': {ex}");
            }
        }

        private static BsonDocument ConvertToBsonDocument(TraceData traceData)
        {
            return new BsonDocument
            {
                { "Timestamp", traceData.Timestamp },
                { "TraceId", traceData.TraceId },
                { "SpanId", traceData.SpanId },
                { "ParentSpanId", traceData.ParentSpanId },
                { "ParentId", traceData.ParentId },
                { "Kind", traceData.Kind },
                { "ActivitySourceName", traceData.ActivitySourceName },
                { "OperationName", traceData.OperationName },
                { "StartTime", traceData.StartTime },
                { "EndTime", traceData.EndTime },
                { "Duration", traceData.Duration },
                {
                    "Attributes",
                    new BsonDocument(
                        traceData.Attributes.ToDictionary(
                            kvp => kvp.Key,
                            kvp => BsonValue.Create(kvp.Value)
                        )
                    )
                },
                { "Status", traceData.Status },
                { "StatusDescription", traceData.StatusDescription },
                { "Baggage", new BsonDocument(traceData.Baggage) },
                { "ServiceName", traceData.ServiceName },
                { "TenantId", traceData.TenantId }
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                FlushBatchAsync().GetAwaiter().GetResult();
                _timer.Dispose();
                _semaphore.Dispose();
                _messageSender?.Dispose();
            }

            _disposed = true;
            base.Dispose(disposing);
        }
    }
}