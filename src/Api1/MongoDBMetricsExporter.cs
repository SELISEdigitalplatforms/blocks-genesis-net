using MongoDB.Bson;
using MongoDB.Driver;
using Newtonsoft.Json;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Api1
{
    public class MongoDBMetricsExporter : BaseExporter<Metric>
    {
        private readonly IMongoCollection<BsonDocument> _collection;

        public MongoDBMetricsExporter()
        {
            var client = new MongoClient("mongodb://localhost:27017");
            var database = client.GetDatabase("telemetry");
            _collection = database.GetCollection<BsonDocument>("metrics");
        }

        public override ExportResult Export(in Batch<Metric> batch)
        {
            var documents = new List<BsonDocument>();
            foreach (var data in batch)
            {

                foreach (var metricPoint in data.GetMetricPoints())
                {
                    var document = new BsonDocument
                    {
                        { "Name", data.Name },
                        { "Description", data.Description },
                        { "Type", data.MetricType.ToString() },
                        { "Unit", data.Unit.ToString() },
                        { "MeterName", data.MeterName.ToString() },
                        { "Timestamp", DateTime.UtcNow },
                        { "ServiceName", "API" },
                        { "TenantId", "TenantId" },
                        { "Tags", JsonConvert.SerializeObject(metricPoint.Tags) },
                        { "StartTime", metricPoint.StartTime.ToString() },
                        { "EndTime", metricPoint.EndTime.ToString() }
                    };

                    switch (data.MetricType)
                    {
                        case MetricType.LongSum:
                            document["Value"] = metricPoint.GetSumLong();
                            break;
                        case MetricType.DoubleSum:
                            document["Value"] = metricPoint.GetSumDouble();
                            break;
                        case MetricType.LongGauge:
                            document["Value"] = metricPoint.GetGaugeLastValueLong();
                            break;
                        case MetricType.DoubleGauge:
                            document["Value"] = metricPoint.GetGaugeLastValueDouble();
                            break;
                        case MetricType.Histogram:
                            document["Count"] = metricPoint.GetHistogramCount();
                            document["Sum"] = metricPoint.GetHistogramSum();
                            document["BucketCounts"] = new BsonArray(metricPoint.GetHistogramBuckets().ToString());
                            break;
                    }
                    documents.Add(document);
                }

            }

            _collection.InsertManyAsync(documents);

            return ExportResult.Success;

        }
    }

}
