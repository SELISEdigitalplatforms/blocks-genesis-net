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

        private BsonDocument CreateLogBisonDocument(LogEvent logEvent)
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

            if (logEvent?.Properties != null)
            {
                foreach (var property in logEvent.Properties)
                {
                    document[property.Key] = ConvertLogEventPropertyValue(property.Value);
                }
            }

            return document;
        }

        private static BsonValue ConvertLogEventPropertyValue(LogEventPropertyValue propertyValue)
        {
            switch (propertyValue)
            {
                case ScalarValue scalarValue:
                    var value = scalarValue.Value;
                    if (value is DateTimeOffset dto)
                        value = dto.UtcDateTime;
                    return BsonValue.Create(value is string ? value : value?.ToString());

                case SequenceValue sequenceValue:
                    return new BsonArray(sequenceValue.Elements.Select(e => e.ToString()));

                case StructureValue structureValue:
                    return new BsonDocument(
                        structureValue.Properties.Select(p =>
                            new BsonElement(p.Name, p.Value.ToString())));

                default:
                    return propertyValue.ToString();
            }
        }

        public async Task EmitBatchAsync(IReadOnlyCollection<LogEvent> batch)
        {
            var collection = LmtConfiguration.GetMongoCollection<BsonDocument>(_blocksSecret.LogConnectionString, LmtConfiguration.LogDatabaseName, _serviceName);
            var documents = new List<BsonDocument>();

            foreach (var logEvent in batch)
            {
                documents.Add(CreateLogBisonDocument(logEvent));
            }

            await collection.InsertManyAsync(documents);
            documents.Clear();
        }
    }
}
