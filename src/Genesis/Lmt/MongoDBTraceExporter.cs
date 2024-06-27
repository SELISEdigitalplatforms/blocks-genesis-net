using MongoDB.Bson;
using Newtonsoft.Json;
using OpenTelemetry;
using System.Diagnostics;

namespace Blocks.Genesis
{
    public class MongoDBTraceExporter : BaseProcessor<Activity>
    {
        private string _serviceName;

        public MongoDBTraceExporter(string serviceName)
        {
            _serviceName = serviceName;
        }

        public override async void OnEnd(Activity data)
        {
            var collection = LmtConfiguration.GetMongoCollection<BsonDocument>(LmtConfiguration.TraceDatabaseName, _serviceName);

            var endTime = DateTime.Now;
            var document = new BsonDocument
            {
                { "Timestamp", DateTime.UtcNow },
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
                { "ServiceName", _serviceName },
                { "TenantId", "TenantId" }
            };

            await collection.InsertOneAsync(document);
        }
    }

}
