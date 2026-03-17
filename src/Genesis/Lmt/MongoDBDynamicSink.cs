using MongoDB.Bson;
using MongoDB.Driver;
using SeliseBlocks.LMT.Client;
using Serilog.Core;
using Serilog.Events;

namespace Blocks.Genesis
{
    public class MongoDBDynamicSink : IBatchedLogEventSink
    {
        private readonly string _serviceName;
        private readonly IMongoDatabase? _database;
        private readonly LmtServiceBusSender? _serviceBusSender;
        private bool _disposed;

        public MongoDBDynamicSink(
            string serviceName,
            IBlocksSecret blocksSecret)
        {
            _serviceName = serviceName;

            var connectionString = blocksSecret?.LogConnectionString ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                _database = LmtConfiguration.GetMongoDatabase(connectionString, LmtConfiguration.LogDatabaseName);
            }

            _serviceBusSender = TryCreateLogsSender(serviceName);
        }

        private static LmtServiceBusSender? TryCreateLogsSender(string serviceName)
        {
            var connectionString = LmtConfigurationProvider.GetServiceBusConnectionString();
            if (string.IsNullOrWhiteSpace(connectionString))
                return null;

            var maxRetries = LmtConfigurationProvider.GetMaxRetries();
            var maxFailedBatches = LmtConfigurationProvider.GetMaxFailedBatches();

            return new LmtServiceBusSender(serviceName, connectionString, maxRetries, maxFailedBatches);
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

            if (_serviceBusSender != null)
            {
                await _serviceBusSender.SendLogsAsync(logDataList);
            }

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

        public async Task SaveToMongoDBAsync(List<LogData> logs)
        {
            var collection = _database!.GetCollection<BsonDocument>(_serviceName);

            try
            {
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
                { "Level", logData.Level },
                { "Message", logData.Message },
                { "Exception", logData.Exception },
                { "ServiceName", logData.ServiceName }
            };

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

            _serviceBusSender?.Dispose();
            _disposed = true;
        }
    }
}