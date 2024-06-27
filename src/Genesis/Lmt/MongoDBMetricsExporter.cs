using MongoDB.Bson;
using Newtonsoft.Json;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Blocks.Genesis
{
    public class MongoDBMetricsExporter : BaseExporter<Metric>
    {
        private readonly string _serviceName;

        public MongoDBMetricsExporter(string serviceName)
        {
            _serviceName = serviceName;
        }

        public override ExportResult Export(in Batch<Metric> batch)
        {
            var collection = LmtConfiguration.GetMongoCollection<BsonDocument>(LmtConfiguration.MetricDatabaseName, _serviceName);

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
                        { "ServiceName", _serviceName },
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

            var insertTask = Task.Run(async () => await collection.InsertManyAsync(documents));
            insertTask.Wait();

            return ExportResult.Success;

        }
    }

}
