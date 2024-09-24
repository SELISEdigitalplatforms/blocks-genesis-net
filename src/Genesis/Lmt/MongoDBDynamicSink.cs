using MongoDB.Bson;
using Newtonsoft.Json;
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
                {"_id", Guid.NewGuid().ToString()},
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
