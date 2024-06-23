using MongoDB.Bson;
using MongoDB.Driver;
using Newtonsoft.Json;
using Serilog.Core;
using Serilog.Events;

namespace Api1
{
    public class MongoDBDynamicSink : ILogEventSink
    {
        private readonly IMongoClient _mongoClient;
        private readonly string _databaseName;

        public MongoDBDynamicSink()
        {
            _mongoClient = new MongoClient("mongodb://localhost:27017");
            _databaseName = "logs";
        }

        public void Emit(LogEvent logEvent)
        {
            var database = _mongoClient.GetDatabase(_databaseName);
            var collection = database.GetCollection<BsonDocument>("API");

            var document = new BsonDocument
            {
                { "Timestamp", logEvent?.Timestamp.UtcDateTime },
                { "MessageTemplate", logEvent?.MessageTemplate.Text },
                { "Level", logEvent?.Level.ToString()  ?? string.Empty },
                { "Message", logEvent?.RenderMessage() },
                { "Exception", logEvent?.Exception?.ToString() ?? string.Empty }
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
