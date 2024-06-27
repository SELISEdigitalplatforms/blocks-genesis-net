using MongoDB.Bson;
using Newtonsoft.Json;
using Serilog.Core;
using Serilog.Events;

namespace Blocks.Genesis
{
    public class MongoDBDynamicSink : IBatchedLogEventSink
    {
        private readonly string _serviceName;

        public MongoDBDynamicSink(string serviceName)
        {
            _serviceName = serviceName;
        }

        private BsonDocument CreateLogBsonDocument(LogEvent logEvent)
        {
            var document = new BsonDocument
            {
                { "Timestamp", logEvent?.Timestamp.UtcDateTime },
                { "MessageTemplate", logEvent?.MessageTemplate.Text },
                { "Level", logEvent?.Level.ToString()  ?? string.Empty },
                { "Message", logEvent?.RenderMessage() },
                { "Exception", logEvent?.Exception?.ToString() ?? string.Empty },
                { "ServiceName", _serviceName },
            };

            if (logEvent?.Properties != null)
            {
                foreach (var property in logEvent.Properties)
                {
                    var prop = property.Value.ToString();
                    try
                    {
                        document[property.Key] = JsonConvert.DeserializeObject<string>(prop);
                    }
                    catch (Exception)
                    {
                        document[property.Key] = prop;
                    }

                }
            }

            return document;
        }

        public async Task EmitBatchAsync(IReadOnlyCollection<LogEvent> batch)
        {
            var collection = LmtConfiguration.GetMongoCollection<BsonDocument>(LmtConfiguration.LogDatabaseName, _serviceName);
            var documents = new List<BsonDocument>();
            foreach (var logEvent in batch)
            {
                documents.Add(CreateLogBsonDocument(logEvent));
            }

            await collection.InsertManyAsync(documents);
        }
    }
}
