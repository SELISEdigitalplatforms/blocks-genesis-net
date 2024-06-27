using MongoDB.Bson;
using MongoDB.Driver;
using Newtonsoft.Json;
using OpenTelemetry;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Blocks.Genesis
{
    public class MongoDBTraceExporter : BaseProcessor<Activity>, IDisposable
    {
        private readonly string _serviceName;
        private readonly ConcurrentBag<BsonDocument> _batch;
        private readonly Timer _timer;
        private readonly IMongoCollection<BsonDocument> _collection;
        private readonly int _batchSize;
        private readonly TimeSpan _flushInterval;

        public MongoDBTraceExporter(string serviceName, int batchSize = 1000, TimeSpan? flushInterval = null)
        {
            _serviceName = serviceName;
            _batchSize = batchSize;
            _flushInterval = flushInterval ?? TimeSpan.FromSeconds(7);
            _batch = new ConcurrentBag<BsonDocument>();
            _collection = LmtConfiguration.GetMongoCollection<BsonDocument>(LmtConfiguration.TraceDatabaseName, _serviceName);
            _timer = new Timer(FlushBatch, null, _flushInterval, _flushInterval);
        }

        public override void OnEnd(Activity data)
        {
            var endTime = DateTime.Now;
            var document = new BsonDocument
            {
                { "Timestamp", DateTime.UtcNow },
                { "TraceId", data.TraceId.ToString() },
                { "SpanId", data.SpanId.ToString() },
                { "ParentSpanId", data.ParentSpanId.ToString() },
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
                { "TenantId", "TenantId" }
            };

            _batch.Add(document);
            if (_batch.Count >= _batchSize)
            {
                FlushBatch(null);
            }
        }

        private async void FlushBatch(object? state)
        {
            if (_batch.IsEmpty)
            {
                return;
            }

            var documentsToInsert = new List<BsonDocument>();
            while (_batch.TryTake(out var document))
            {
                documentsToInsert.Add(document);
            }

            if (documentsToInsert.Any())
            {
                try
                {
                    await _collection.InsertManyAsync(documentsToInsert);
                }
                catch (Exception ex)
                {
                    // Log or handle the exception as necessary
                    Console.WriteLine($"Failed to insert batch: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
            FlushBatch(null);
            base.Dispose();
        }
    }
}
