using MongoDB.Bson;
using MongoDB.Driver;
using Newtonsoft.Json;
using OpenTelemetry;
using System.Diagnostics;

namespace Api1
{
    public class MongoDBTraceExporter : BaseProcessor<Activity>
    {
        private readonly IMongoCollection<BsonDocument> _collection;

        public MongoDBTraceExporter()
        {
            var client = new MongoClient("mongodb://localhost:27017");
            var database = client.GetDatabase("telemetry");
            _collection = database.GetCollection<BsonDocument>("traces");
        }

        public override void OnEnd(Activity data)
        {
            var endTime = DateTime.Now;
            var document = new BsonDocument
            {
                { "TraceId", data.TraceId.ToString() },
                { "SpanId", data.SpanId.ToString() },
                { "ParentSpanId", data.ParentSpanId.ToString() },
                { "Kind", data.Kind.ToString() },
                { "ActivitySourceName", data.Source.Name.ToString() },
                { "OperationName", data.DisplayName },
                { "StartTime", data.StartTimeUtc },
                { "EndTime", endTime },
                { "Duration", data.Duration.ToString() },
                { "Attributes", new BsonDocument(data?.Tags?.ToDictionary() ?? new Dictionary<string, string?>()) },
                { "Status", data?.Status.ToString() ?? string.Empty },
                { "StatusDescription", data?.StatusDescription ?? string.Empty },
                { "Baggage", JsonConvert.SerializeObject(data?.Baggage) },
                { "ServiceName", "API" },
                { "TenantId", "TenantId" }
            };

            _collection.InsertOne(document);
        }
    }

}
