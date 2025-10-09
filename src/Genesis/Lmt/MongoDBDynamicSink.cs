using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson;
using MongoDB.Driver;
using Serilog.Core;
using Serilog.Events;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Blocks.Genesis
{
    public class LogData
    {
        public DateTime Timestamp { get; set; }
        public string MessageTemplate { get; set; } = string.Empty;
        public string Level { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Exception { get; set; } = string.Empty;
        public string ServiceName { get; set; } = string.Empty;
        public Dictionary<string, object> Properties { get; set; } = new();
    }

    public class FailedLogBatch
    {
        public List<LogData> Logs { get; set; } = new();
        public int RetryCount { get; set; }
        public DateTime NextRetryTime { get; set; }
    }  

    public class MongoDBDynamicSink : IBatchedLogEventSink
    {
        private readonly string _serviceName;
        private readonly IBlocksSecret _blocksSecret;
        private readonly ConcurrentQueue<FailedLogBatch> _failedBatches;
        private readonly Timer _retryTimer;
        private readonly IMongoDatabase? _database;
        private readonly int _maxRetries;
        private readonly int _maxFailedBatches;
        private readonly string? _azureFunctionEndpoint;
        private readonly string? _azureFunctionApiSecret;
        private readonly HttpClient _httpClient;
        private bool _disposed;

        private readonly SemaphoreSlim _retrySemaphore = new SemaphoreSlim(1, 1);

        public MongoDBDynamicSink(
            string serviceName,
            IBlocksSecret blocksSecret)
        {
            _serviceName = serviceName;
            _blocksSecret = blocksSecret;
            _maxRetries = 3;
            _maxFailedBatches = 100;

            _azureFunctionEndpoint = Environment.GetEnvironmentVariable("LMT_LOG_ENDPOINT");
            _azureFunctionApiSecret = Environment.GetEnvironmentVariable("LMT_API_SECRET");

            _failedBatches = new ConcurrentQueue<FailedLogBatch>();

            var connectionString = _blocksSecret?.LogConnectionString ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                _database = LmtConfiguration.GetMongoDatabase(connectionString, LmtConfiguration.LogDatabaseName);
            }

            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(60)
            };

            _retryTimer = new Timer(async _ => await RetryFailedBatchesAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        private static readonly HashSet<string> AllowedMongoProperties = new()
        {
            "TenantId",
            "TraceId",
            "SpanId",
        };

        public async Task EmitBatchAsync(IReadOnlyCollection<LogEvent> batch)
        {
            var logDataList = new List<LogData>();

            foreach (var logEvent in batch)
            {
                var logData = new LogData
                {
                    Timestamp = logEvent.Timestamp.UtcDateTime,
                    Level = logEvent.Level.ToString(),
                    Message = logEvent.RenderMessage(),
                    Exception = logEvent.Exception?.ToString() ?? string.Empty,
                    ServiceName = _serviceName,
                    Properties = new Dictionary<string, object>()
                };

                if (logEvent.Properties != null)
                {
                    foreach (var property in logEvent.Properties)
                    {
                        if (!AllowedMongoProperties.Contains(property.Key))
                            continue;

                        logData.Properties[property.Key] = ConvertLogEventPropertyValue(property.Value);
                    }
                }

                logDataList.Add(logData);
            }

            // If Azure Function endpoint exists, send data there
            if (!string.IsNullOrWhiteSpace(_azureFunctionEndpoint))
            {
                await SendToAzureFunctionAsync(logDataList);
            }

            // Save to MongoDB only if database exists
            if (_database != null)
            {
                await SaveToMongoDBAsync(logDataList);
            }
        }

        private static object ConvertLogEventPropertyValue(LogEventPropertyValue propertyValue)
        {
            switch (propertyValue)
            {
                case ScalarValue scalarValue:
                    var value = scalarValue.Value;
                    if (value is DateTimeOffset dto)
                        value = dto.UtcDateTime;
                    return value is string ? value : value?.ToString() ?? string.Empty;
                case SequenceValue sequenceValue:
                    return sequenceValue.Elements.Select(e => e.ToString()).ToList();
                case StructureValue structureValue:
                    return structureValue.Properties.ToDictionary(
                        p => p.Name,
                        p => p.Value.ToString() ?? string.Empty);
                default:
                    return propertyValue.ToString() ?? string.Empty;
            }
        }

        public async Task SendToAzureFunctionAsync(List<LogData> logs, int retryCount = 0)
        {
            int currentRetry = 0;

            while (currentRetry <= _maxRetries)
            {
                try
                {
                    var json = JsonSerializer.Serialize(new
                    {
                        Type = "logs",
                        ServiceName = _serviceName,
                        Data = logs
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
                        return;
                    }

                    Console.WriteLine($"Failed to send logs to Azure Function: {response.StatusCode}, Retry: {currentRetry}/{_maxRetries}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Exception sending logs to Azure Function: {ex.Message}, Retry: {currentRetry}/{_maxRetries}");
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
                var failedBatch = new FailedLogBatch
                {
                    Logs = logs,
                    RetryCount = retryCount + 1,
                    NextRetryTime = DateTime.UtcNow.AddMinutes(Math.Pow(2, retryCount)) // 1min, 2min, 4min, 8min...
                };

                _failedBatches.Enqueue(failedBatch);
                Console.WriteLine($"Queued log batch for later retry. Failed batches in queue: {_failedBatches.Count}");
            }
            else
            {
                Console.WriteLine($"Failed log batch queue is full ({_maxFailedBatches}). Dropping batch.");
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
                var batchesToRetry = new List<FailedLogBatch>();
                var batchesToRequeue = new List<FailedLogBatch>();

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
                        Console.WriteLine($"Log batch exceeded max retries ({_maxRetries}). Dropping batch with {failedBatch.Logs.Count} logs.");
                        continue;
                    }

                    Console.WriteLine($"Retrying failed log batch (Attempt {failedBatch.RetryCount + 1}/{_maxRetries})");
                    await SendToAzureFunctionAsync(failedBatch.Logs, failedBatch.RetryCount);
                }
            }
            finally
            {
                _retrySemaphore.Release();
            }
        }

        public async Task SaveToMongoDBAsync(List<LogData> logs)
        {
            var collection = _database!.GetCollection<BsonDocument>(_serviceName);

            try
            {
                // Convert LogData to BsonDocument only for MongoDB
                var bsonDocuments = logs.Select(ConvertToBsonDocument).ToList();
                await collection.InsertManyAsync(bsonDocuments);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to insert log batch for service {_serviceName}: {ex.Message}");
            }
        }

        private static BsonDocument ConvertToBsonDocument(LogData logData)
        {
            var document = new BsonDocument
            {
                { "Timestamp", logData.Timestamp },
                { "MessageTemplate", logData.MessageTemplate },
                { "Level", logData.Level },
                { "Message", logData.Message },
                { "Exception", logData.Exception },
                { "ServiceName", logData.ServiceName }
            };

            // Add properties as top-level fields (not nested)
            foreach (var property in logData.Properties)
            {
                try
                {
                    document[property.Key] = ConvertPropertyToBsonValue(property.Value);
                }
                catch
                {
                    document[property.Key] = property.Value?.ToString() ?? string.Empty;
                }
            }

            return document;
        }

        private static BsonValue ConvertPropertyToBsonValue(object value)
        {
            return value switch
            {
                string str => str,
                int i => i,
                long l => l,
                double d => d,
                bool b => b,
                DateTime dt => dt,
                List<object> list => new BsonArray(list.Select(ConvertPropertyToBsonValue)),
                Dictionary<string, object> dict => new BsonDocument(dict.Select(kvp =>
                    new BsonElement(kvp.Key, ConvertPropertyToBsonValue(kvp.Value)))),
                _ => value?.ToString() ?? string.Empty
            };
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _retryTimer.Dispose();
            _retrySemaphore.Dispose();
            _httpClient.Dispose();
            RetryFailedBatchesAsync().GetAwaiter().GetResult();

            _disposed = true;
        }
    }
}