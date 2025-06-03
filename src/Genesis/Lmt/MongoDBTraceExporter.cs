using MongoDB.Bson;
using MongoDB.Driver;
using OpenTelemetry;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Blocks.Genesis
{
    public class MongoDBTraceExporter : BaseProcessor<Activity>, IDisposable
    {
        private readonly string _serviceName;
        private readonly ConcurrentQueue<BsonDocument> _batch;
        private readonly Timer _timer;
        private readonly IMongoDatabase _database;
        private readonly int _batchSize;
        private readonly TimeSpan _flushInterval;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        public MongoDBTraceExporter(string serviceName, int batchSize = 1000, TimeSpan? flushInterval = null, IBlocksSecret? blocksSecret = null)
        {
            _serviceName = serviceName;
            _batchSize = batchSize;
            _flushInterval = flushInterval ?? TimeSpan.FromSeconds(3);
            _batch = new ConcurrentQueue<BsonDocument>();
            _database = LmtConfiguration.GetMongoDatabase(blocksSecret?.TraceConnectionString ?? string.Empty, LmtConfiguration.TraceDatabaseName);
            _timer = new Timer(async _ => await FlushBatchAsync(), null, _flushInterval, _flushInterval);
        }

        public override void OnEnd(Activity data)
        {
            var endTime = data.StartTimeUtc.Add(data.Duration);

            var tenantId = data.GetBaggageItem("TenantId");
            tenantId = !string.IsNullOrWhiteSpace(tenantId) ? tenantId : BlocksConstants.Miscellaneous;

            var document = new BsonDocument
            {
                { "Timestamp", endTime },
                { "TraceId", data.TraceId.ToString() },
                { "SpanId", data.SpanId.ToString() },
                { "ParentSpanId", data.ParentSpanId.ToString() },
                { "ParentId", data.ParentId?.ToString() ?? string.Empty },
                { "Kind", data.Kind.ToString() },
                { "ActivitySourceName", data.Source.Name.ToString() },
                { "OperationName", data.DisplayName },
                { "StartTime", data.StartTimeUtc },
                { "EndTime", endTime },
                { "Duration", data.Duration.TotalMilliseconds },
                { "Attributes", new BsonDocument(data.Tags?.ToDictionary(kvp => kvp.Key, kvp => (BsonValue)kvp.Value) ?? new Dictionary<string, BsonValue>()) },
                { "Status", data.Status.ToString() },
                { "StatusDescription", data.StatusDescription ?? string.Empty },
                { "Baggage", new BsonArray(data.Baggage?.Select(kvp => new BsonDocument(kvp.Key, kvp.Value))) },
                { "ServiceName", _serviceName },
                { "TenantId", tenantId }
            };

            // Add the document to the batch
            _batch.Enqueue(document);

            // If the batch size is reached, trigger batch insert
            if (_batch.Count >= _batchSize)
            {
                Task.Run(() => FlushBatchAsync());
            }
        }

        private static BsonValue TryConvertToBsonValue(object? value)
        {
            return value switch
            {
                null => BsonNull.Value,
                string str => BsonValue.Create(str),
                _ => BsonValue.Create(value)
            };
        }

        private async Task FlushBatchAsync()
        {
            await _semaphore.WaitAsync();
            try
            {
                // Dictionary to hold lists of documents per tenant
                var tenantBatches = new Dictionary<string, List<BsonDocument>>();

                // Group documents by TenantId
                while (_batch.TryDequeue(out var document))
                {
                    var tenantId = document["TenantId"].AsString;
                    if (!tenantBatches.ContainsKey(tenantId))
                    {
                        tenantBatches[tenantId] = new List<BsonDocument>();
                    }
                    tenantBatches[tenantId].Add(document);
                }

                // Perform bulk insert for each tenant
                foreach (var tenantBatch in tenantBatches)
                {
                    var collection = _database.GetCollection<BsonDocument>(tenantBatch.Key);

                    try
                    {
                        // Bulk insert
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

        void IDisposable.Dispose()
        {
            _timer.Dispose();
            FlushBatchAsync().GetAwaiter().GetResult();
            _semaphore.Dispose();
            base.Dispose();
        }
    }
}
