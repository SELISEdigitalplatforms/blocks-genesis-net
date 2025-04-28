using MongoDB.Bson;
using Serilog.Core;
using Serilog.Events;

namespace Blocks.Genesis
{
    public class MongoDBDynamicSink : IBatchedLogEventSink
    {
        private readonly string _serviceName;
        private readonly IBlocksSecret _blocksSecret;

        public MongoDBDynamicSink(string serviceName, IBlocksSecret blocksSecret)
        {
            _serviceName = serviceName;
            _blocksSecret = blocksSecret;
        }

        private BsonDocument CreateLogBsonDocument(LogEvent logEvent)
        {
            var document = new BsonDocument
            {
                { "Timestamp", logEvent?.Timestamp.UtcDateTime },
                { "MessageTemplate", logEvent?.MessageTemplate.Text },
                { "Level", logEvent?.Level.ToString() ?? string.Empty },
                { "Message", logEvent?.RenderMessage() },
                { "Exception", logEvent?.Exception?.ToString() ?? string.Empty },
                { "ServiceName", _serviceName },
            };

            if (logEvent.Properties != null)
            {
                foreach (var property in logEvent.Properties)
                {
                    var propertyValue = property.Value;

                    if (propertyValue is ScalarValue scalarValue)
                    {
                        var value = scalarValue.Value;

                        if (value is DateTimeOffset dto)
                        {
                            value = dto.UtcDateTime; 
                        }

                        document[property.Key] = BsonValue.Create(value is string ? value : value?.ToString()); 
                    }

                    else if (propertyValue is SequenceValue sequenceValue)
                    {
                        document[property.Key] = new BsonArray(sequenceValue.Elements.Select(e => e.ToString())); // Handle sequences
                    }

                    else if (propertyValue is StructureValue structureValue)
                    {
                        document[property.Key] = new BsonDocument(
                            structureValue.Properties.Select(p =>
                                new BsonElement(p.Name, p.Value.ToString()))); // Handle structured values
                    }

                    else
                    {
                        document[property.Key] = propertyValue.ToString(); // Fallback for unhandled cases
                    }
                }
            }

            return document;
        }

        public async Task EmitBatchAsync(IReadOnlyCollection<LogEvent> batch)
        {
            var collection = LmtConfiguration.GetMongoCollection<BsonDocument>(_blocksSecret.LogConnectionString, LmtConfiguration.LogDatabaseName, _serviceName);
            var documents = new List<BsonDocument>();

            foreach (var logEvent in batch)
            {
                documents.Add(CreateLogBsonDocument(logEvent));
            }

            await collection.InsertManyAsync(documents);
            documents = null;
        }
    }
}
