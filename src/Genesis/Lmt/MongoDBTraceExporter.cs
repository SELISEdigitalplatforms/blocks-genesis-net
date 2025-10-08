using MongoDB.Bson;
using MongoDB.Driver;
using OpenTelemetry;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Blocks.Genesis
{
    public class TraceData
    {
        public DateTime Timestamp { get; set; }
        public string TraceId { get; set; } = string.Empty;
        public string SpanId { get; set; } = string.Empty;
        public string ParentSpanId { get; set; } = string.Empty;
        public string ParentId { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string ActivitySourceName { get; set; } = string.Empty;
        public string OperationName { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public double Duration { get; set; }
        public Dictionary<string, object?> Attributes { get; set; } = new();
        public string Status { get; set; } = string.Empty;
        public string StatusDescription { get; set; } = string.Empty;
        public Dictionary<string, string> Baggage { get; set; } = new();
        public string ServiceName { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
    }

    public class FailedBatch
    {
        public Dictionary<string, List<TraceData>> TenantBatches { get; set; } = new();
        public int RetryCount { get; set; }
        public DateTime NextRetryTime { get; set; }
    }

    public class MongoDBTraceExporter : BaseProcessor<Activity>
    {
        private readonly string _serviceName;
        private readonly ConcurrentQueue<TraceData> _batch;
        private readonly ConcurrentQueue<FailedBatch> _failedBatches;
        private readonly Timer _timer;
        private readonly Timer _retryTimer;
        private readonly IMongoDatabase? _database;
        private readonly int _batchSize;
        private readonly int _maxRetries;
        private readonly int _maxFailedBatches;
        private readonly string? _azureFunctionEndpoint;
        private readonly string? _azureFunctionApiSecret;
        private readonly HttpClient _httpClient;
        private bool _disposed;

        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _retrySemaphore = new SemaphoreSlim(1, 1);

        public MongoDBTraceExporter(
            string serviceName,
            int batchSize = 1000,
            IBlocksSecret? blocksSecret = null)
        {
            _serviceName = serviceName;
            _batchSize = batchSize;
            _maxRetries = 3;
            _maxFailedBatches = 100;

            _azureFunctionEndpoint = Environment.GetEnvironmentVariable("LMT_TRACE_ENDPOINT");
            _azureFunctionApiSecret = Environment.GetEnvironmentVariable("LMT_API_SECRET");

            var interval = TimeSpan.FromSeconds(3);
            _batch = new ConcurrentQueue<TraceData>();
            _failedBatches = new ConcurrentQueue<FailedBatch>();

            var connectionString = blocksSecret?.TraceConnectionString ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                _database = LmtConfiguration.GetMongoDatabase(connectionString, LmtConfiguration.TraceDatabaseName);
            }

            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(60)
            };

            _timer = new Timer(async _ => await FlushBatchAsync(), null, interval, interval);
            _retryTimer = new Timer(async _ => await RetryFailedBatchesAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
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

                // If Azure Function endpoint exists, send data there
                if (!string.IsNullOrWhiteSpace(_azureFunctionEndpoint))
                {
                    await SendToAzureFunctionAsync(tenantBatches);
                }

                // Save to MongoDB only if database exists
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

        private async Task SendToAzureFunctionAsync(Dictionary<string, List<TraceData>> tenantBatches, int retryCount = 0)
        {
            int currentRetry = 0;

            while (currentRetry <= _maxRetries)
            {
                try
                {
                    var json = JsonSerializer.Serialize(new
                    {
                        Type = "traces",
                        Data = tenantBatches,
                        ServiceName = _serviceName
                    });
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    if (!string.IsNullOrWhiteSpace(_azureFunctionApiSecret))
                    {
                        _httpClient.DefaultRequestHeaders.Clear();
                        _httpClient.DefaultRequestHeaders.Add("x-functions-key", _azureFunctionApiSecret);
                    }

                    var response = await _httpClient.PostAsync(_azureFunctionEndpoint, content);

                    if (response.IsSuccessStatusCode)
                    {
                        // Success - exit retry loop
                        return;
                    }

                    Console.WriteLine($"Failed to send batch to Azure Function: {response.StatusCode}, Retry: {currentRetry}/{_maxRetries}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Exception sending batch to Azure Function: {ex.Message}, Retry: {currentRetry}/{_maxRetries}");
                }

                currentRetry++;

                if (currentRetry <= _maxRetries)
                {
                    // Exponential backoff: 1s, 2s, 4s, 8s...
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, currentRetry - 1));
                    await Task.Delay(delay);
                }
            }

            // All retries failed - queue for later retry
            if (_failedBatches.Count < _maxFailedBatches)
            {
                var failedBatch = new FailedBatch
                {
                    TenantBatches = tenantBatches,
                    RetryCount = retryCount + 1,
                    NextRetryTime = DateTime.UtcNow.AddMinutes(Math.Pow(2, retryCount)) // 1min, 2min, 4min, 8min...
                };

                _failedBatches.Enqueue(failedBatch);
                Console.WriteLine($"Queued batch for later retry. Failed batches in queue: {_failedBatches.Count}");
            }
            else
            {
                Console.WriteLine($"Failed batch queue is full ({_maxFailedBatches}). Dropping batch.");
            }
        }

        private async Task RetryFailedBatchesAsync()
        {
            if (!await _retrySemaphore.WaitAsync(0))
            {
                // Another retry is in progress, skip this iteration
                return;
            }

            try
            {
                var now = DateTime.UtcNow;
                var batchesToRetry = new List<FailedBatch>();
                var batchesToRequeue = new List<FailedBatch>();

                // Collect batches that are ready for retry
                while (_failedBatches.TryDequeue(out var failedBatch))
                {
                    if (failedBatch.NextRetryTime <= now)
                    {
                        batchesToRetry.Add(failedBatch);
                    }
                    else
                    {
                        batchesToRequeue.Add(failedBatch);
                    }
                }

                // Requeue batches that aren't ready yet
                foreach (var batch in batchesToRequeue)
                {
                    _failedBatches.Enqueue(batch);
                }

                // Retry ready batches
                foreach (var failedBatch in batchesToRetry)
                {
                    if (failedBatch.RetryCount >= _maxRetries)
                    {
                        Console.WriteLine($"Batch exceeded max retries ({_maxRetries}). Dropping batch with {failedBatch.TenantBatches.Sum(x => x.Value.Count)} traces.");
                        continue;
                    }

                    Console.WriteLine($"Retrying failed batch (Attempt {failedBatch.RetryCount + 1}/{_maxRetries})");
                    await SendToAzureFunctionAsync(failedBatch.TenantBatches, failedBatch.RetryCount);
                }
            }
            finally
            {
                _retrySemaphore.Release();
            }
        }

        private async Task SaveToMongoDBAsync(Dictionary<string, List<TraceData>> tenantBatches)
        {
            foreach (var tenantBatch in tenantBatches)
            {
                var collection = _database!.GetCollection<BsonDocument>(tenantBatch.Key);

                try
                {
                    // Convert TraceData to BsonDocument only for MongoDB
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
                _retryTimer.Dispose();
                _semaphore.Dispose();
                _retrySemaphore.Dispose();
                _httpClient.Dispose();
                FlushBatchAsync().GetAwaiter().GetResult();
                RetryFailedBatchesAsync().GetAwaiter().GetResult();
            }

            _disposed = true;
            base.Dispose(disposing);
        }
    }
}