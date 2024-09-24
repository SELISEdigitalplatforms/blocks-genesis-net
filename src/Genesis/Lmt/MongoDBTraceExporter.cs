using MongoDB.Bson;
using MongoDB.Driver;
using Newtonsoft.Json;
using OpenTelemetry;
using System.Diagnostics;

namespace Blocks.Genesis
{
    public class MongoDBTraceExporter : BaseProcessor<Activity>, IDisposable
    {
        private readonly string _serviceName;
        private readonly Queue<BsonDocument> _batch;
        private readonly Timer _timer;
        private readonly IMongoCollection<BsonDocument> _collection;
        private readonly int _batchSize;
        private readonly TimeSpan _flushInterval;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        public MongoDBTraceExporter(string serviceName, int batchSize = 1000, TimeSpan? flushInterval = null, IBlocksSecret blocksSecret = null)
        {
            _serviceName = serviceName;
            _batchSize = batchSize;
            _flushInterval = flushInterval ?? TimeSpan.FromSeconds(3);            
            _batch = new Queue<BsonDocument>();
            _collection = LmtConfiguration.GetMongoCollection<BsonDocument>(blocksSecret.TraceConnectionString, LmtConfiguration.TraceDatabaseName, _serviceName);
            _timer = new Timer(async _ => await FlushBatchAsync(), null, _flushInterval, _flushInterval);        
        }

        public override void OnEnd(Activity data)
        {
            var endTime = DateTime.Now;
            var document = new BsonDocument
            {
                {"_id", Guid.NewGuid().ToString()},
                { "Timestamp", DateTime.UtcNow },
                { "TraceId", data.TraceId.ToString() },
                { "ParentTraceId", string.IsNullOrWhiteSpace(data.TraceStateString) ? string.Empty : data.TraceStateString},
                { "SpanId", data.SpanId.ToString() },
                { "ParentSpanId", data.ParentSpanId.ToString() },
                { "ParentId", data.ParentId?.ToString() ?? string.Empty },
                { "Kind", data.Kind.ToString() },
                { "ActivitySourceName", data.Source.Name.ToString() },
                { "OperationName", data.DisplayName },
                { "StartTime", data.StartTimeUtc },
                { "EndTime", endTime },
                { "Duration", data.Duration.ToString() },
                { "Attributes", new BsonDocument(data?.Tags?.ToDictionary() ?? new Dictionary<string, string?>()) },
                { "Status", data?.Status.ToString() ?? string.Empty },
                { "StatusDescription", data?.StatusDescription ?? string.Empty },
                { "Baggage", JsonConvert.SerializeObject(data?.Baggage) },
                { "ServiceName", _serviceName },
                { "TenantId", data?.GetCustomProperty("TenantId")?.ToString() ?? string.Empty },
                { "Request", data?.GetCustomProperty("RequestInfo")?.ToString() ?? string.Empty },
                { "Response", data?.GetCustomProperty("ResponseInfo")?.ToString() ?? string.Empty }
            };

            _batch.Enqueue(document);
            if (_batch.Count >= _batchSize)
            {
                Task.Run(() => FlushBatchAsync());
            }
        }

        private async Task FlushBatchAsync()
        {
            await _semaphore.WaitAsync();
            try
            {
                var batchToInsert = new List<BsonDocument>();

                while (_batch.TryDequeue(out var document))
                {
                    batchToInsert.Add(document);
                }

                if (batchToInsert.Any())
                {
                    try
                    {
                        await _collection.InsertManyAsync(batchToInsert);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to insert batch: {ex.Message}");
                    }
                    finally
                    {
                        batchToInsert = null;
                    }
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
            FlushBatchAsync().GetAwaiter().GetResult();
            _semaphore.Dispose();
            base.Dispose();
        }
    }
}
