using MongoDB.Bson;
using MongoDB.Driver;
using Newtonsoft.Json;
using Serilog.Core;
using Serilog.Events;

namespace Blocks.Genesis
{
    public class MongoDBDynamicSink : ILogEventSink
    {
        private readonly string _serviceName;
        private readonly IMongoClient _mongoClient;
        private readonly string _databaseName;

        public MongoDBDynamicSink(string serviceName)
        {
            _serviceName = serviceName;
            _mongoClient = new MongoClient("mongodb://localhost:27017");
            _databaseName = "Logs";
        }

        public void Emit(LogEvent logEvent)
        {
            var database = _mongoClient.GetDatabase(_databaseName);
            var collection = database.GetCollection<BsonDocument>("Logs"); // tenant wise

            var document = new BsonDocument
            {
                { "Timestamp", logEvent?.Timestamp.UtcDateTime },
                { "MessageTemplate", logEvent?.MessageTemplate.Text },
                { "Level", logEvent?.Level.ToString()  ?? string.Empty },
                { "Message", logEvent?.RenderMessage() },
                { "Exception", logEvent?.Exception?.ToString() ?? string.Empty },
                { "ServiceName", _serviceName },
            };

            if(logEvent?.Properties != null)
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

            collection.InsertOne(document);
        }
    }
}
