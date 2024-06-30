using MongoDB.Bson;
using MongoDB.Driver;
using Newtonsoft.Json;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Blocks.Genesis
{
    public class MongoDBMetricsExporter : BaseExporter<Metric>
    {
        private readonly string _serviceName;
        private readonly IMongoCollection<BsonDocument> _collection;

        public MongoDBMetricsExporter(string serviceName)
        {
            _serviceName = serviceName;
            _collection = LmtConfiguration.GetMongoCollection<BsonDocument>(LmtConfiguration.MetricDatabaseName, _serviceName);
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
                        { "Unit", data.Unit },
                        { "MeterName", data.MeterName },
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

            try
            {
                _collection.InsertMany(documents);
                return ExportResult.Success;
            }
            catch (Exception ex)
            {
                // Log the exception
                Console.WriteLine($"Error exporting metrics: {ex.Message}");
                return ExportResult.Failure;
            }
            finally
            {
                documents = null; // Explicitly setting the documents list to null
            }
        }
    }
}
