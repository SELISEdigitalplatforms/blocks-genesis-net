using Blocks.LMT.Client;
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
        private readonly ConcurrentQueue<TraceData> _batch;
        private readonly Timer _timer;
        private readonly IMongoDatabase? _database;
        private readonly int _batchSize;
        private readonly LmtServiceBusSender? _serviceBusSender;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private bool _disposed;

        public MongoDBTraceExporter(
            string serviceName,
            int batchSize = 1000,
            IBlocksSecret? blocksSecret = null)
        {
            _serviceName = serviceName;
            _batchSize = batchSize;

            var interval = TimeSpan.FromSeconds(3);
            _batch = new ConcurrentQueue<TraceData>();

            var connectionString = blocksSecret?.TraceConnectionString ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                _database = LmtConfiguration.GetMongoDatabase(connectionString, LmtConfiguration.TraceDatabaseName);
            }

            _serviceBusSender = TryCreateTracesSender(serviceName);

            _timer = new Timer(async _ => await FlushBatchAsync(), null, interval, interval);
        }

        private static LmtServiceBusSender? TryCreateTracesSender(string serviceName)
        {
            var connectionString = LmtConfigurationProvider.GetServiceBusConnectionString();
            if (string.IsNullOrWhiteSpace(connectionString))
                return null;

            var maxRetries = LmtConfigurationProvider.GetMaxRetries();
            var maxFailedBatches = LmtConfigurationProvider.GetMaxFailedBatches();

            return new LmtServiceBusSender(serviceName, connectionString, maxRetries, maxFailedBatches);
        }

        public override void OnEnd(Activity data)
        {
            var endTime = data.StartTimeUtc.Add(data.Duration);
            var tenantId = Baggage.GetBaggage("TenantId") ?? BlocksConstants.Miscellaneous;
            tenantId = !string.IsNullOrWhiteSpace(tenantId) ? tenantId : BlocksConstants.Miscellaneous;

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

            if (_batch.Count >= _batchSize)
            {
                Task.Run(() => FlushBatchAsync());
            }
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
                    if (!tenantBatches.ContainsKey(traceData.TenantId))
                    {
                        tenantBatches[traceData.TenantId] = [];
                    }
                    tenantBatches[traceData.TenantId].Add(traceData);
                }

                if (tenantBatches.Count == 0)
                    return;

                if (_serviceBusSender != null)
                {
                    await _serviceBusSender.SendTracesAsync(tenantBatches);
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
            foreach (var tenantBatch in tenantBatches)
            {
                var collection = _database!.GetCollection<BsonDocument>(tenantBatch.Key);

                try
                {
                    var bsonDocuments = tenantBatch.Value.Select(ConvertToBsonDocument).ToList();
                    await collection.InsertManyAsync(bsonDocuments);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to insert batch for tenant {tenantBatch.Key}: {ex.Message}");
                }
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
                _timer.Dispose();
                _semaphore.Dispose();
                FlushBatchAsync().GetAwaiter().GetResult();
                _serviceBusSender?.Dispose();
            }

            _disposed = true;
            base.Dispose(disposing);
        }
    }
}