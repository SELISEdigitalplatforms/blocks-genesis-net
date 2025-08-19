using MongoDB.Bson;
using MongoDB.Driver;
using OpenTelemetry;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Blocks.Genesis
{
    public class MongoDBTraceExporter : BaseProcessor<Activity>
    {
        private readonly string _serviceName;
        private readonly ConcurrentQueue<BsonDocument> _batch;
        private readonly Timer _timer;
        private readonly IMongoDatabase _database;
        private readonly int _batchSize;
        private bool _disposed;

        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        public MongoDBTraceExporter(string serviceName, int batchSize = 1000, TimeSpan? flushInterval = null, IBlocksSecret? blocksSecret = null)
        {
            _serviceName = serviceName;
            _batchSize = batchSize;
             var interval = flushInterval ?? TimeSpan.FromSeconds(3);
            _batch = new ConcurrentQueue<BsonDocument>();
            _database = LmtConfiguration.GetMongoDatabase(blocksSecret?.TraceConnectionString ?? string.Empty, LmtConfiguration.TraceDatabaseName);
            _timer = new Timer(async _ => await FlushBatchAsync(), null, interval, interval);
        }

        public override void OnEnd(Activity data)
        {
            var endTime = data.StartTimeUtc.Add(data.Duration);
            var tenantId = Baggage.GetBaggage("TenantId") ?? BlocksConstants.Miscellaneous;
            tenantId = !string.IsNullOrWhiteSpace(tenantId) ? tenantId : BlocksConstants.Miscellaneous;

            var document = new BsonDocument
            {
                { "Timestamp", endTime },
                { "TraceId", data.TraceId.ToString() },
                { "SpanId", data.SpanId.ToString() },
                { "ParentSpanId", data.ParentSpanId.ToString() },
                { "ParentId", data.ParentId?.ToString() ?? string.Empty },
                { "Kind", data.Kind.ToString() },
                { "ActivitySourceName", data.Source.Name },
                { "OperationName", data.DisplayName },
                { "StartTime", data.StartTimeUtc },
                { "EndTime", endTime },
                { "Duration", data.Duration.TotalMilliseconds },
                {
                    "Attributes",
                    new BsonDocument(
                        data.TagObjects?.ToDictionary(
                            kvp => kvp.Key,
                            kvp => BsonValue.Create(kvp.Value)
                        ) ?? new Dictionary<string, BsonValue>()
                    )
                },
                { "Status", data.Status.ToString() },
                { "StatusDescription", data.StatusDescription ?? string.Empty },
                { "Baggage", GetBaggageItems() },
                { "ServiceName", _serviceName },
                { "TenantId", tenantId }
            };

            _batch.Enqueue(document);

            if (_batch.Count >= _batchSize)
            {
                Task.Run(() => FlushBatchAsync());
            }
        }

        private static BsonDocument GetBaggageItems()
        {
            var baggageDoc = new BsonDocument();

            foreach (var baggageItem in Baggage.Current)
            {
                baggageDoc[baggageItem.Key] = baggageItem.Value;
            }

            return baggageDoc;
        }

        private async Task FlushBatchAsync()
        {
            await _semaphore.WaitAsync();
            try
            {
                var tenantBatches = new Dictionary<string, List<BsonDocument>>();

                while (_batch.TryDequeue(out var document))
                {
                    var tenantId = document["TenantId"].AsString;
                    if (!tenantBatches.ContainsKey(tenantId))
                    {
                        tenantBatches[tenantId] = [];
                    }
                    tenantBatches[tenantId].Add(document);
                }

                foreach (var tenantBatch in tenantBatches)
                {
                    var collection = _database.GetCollection<BsonDocument>(tenantBatch.Key);

                    try
                    {
                        await collection.InsertManyAsync(tenantBatch.Value);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to insert batch for tenant {tenantBatch.Key}: {ex.Message}");
                    }
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                _timer.Dispose();
                _semaphore.Dispose();
                FlushBatchAsync().GetAwaiter().GetResult();
            }

            _disposed = true;
            base.Dispose();
        }
    }
}
