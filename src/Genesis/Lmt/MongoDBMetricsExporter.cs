using MongoDB.Bson;
using MongoDB.Driver;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using System.Text.Json;

namespace Blocks.Genesis
{
    public class MongoDBMetricsExporter : BaseExporter<Metric>
    {
        private readonly string _serviceName;
        private const string _value = "Value";

        public MongoDBMetricsExporter(string serviceName, IBlocksSecret blocksSecret)
        {
            _serviceName = serviceName;
            LmtConfiguration.GetMongoCollection<BsonDocument>(blocksSecret.MetricConnectionString, LmtConfiguration.MetricDatabaseName, _serviceName);
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
                        { "Tags", JsonSerializer.Serialize(metricPoint.Tags) },
                        { "StartTime", metricPoint.StartTime.UtcDateTime },
                        { "EndTime", metricPoint.EndTime.UtcDateTime }
                    };

                    switch (data.MetricType)
                    {
                        case MetricType.LongSum:
                            document[_value] = metricPoint.GetSumLong();
                            break;
                        case MetricType.DoubleSum:
                            document[_value] = metricPoint.GetSumDouble();
                            break;
                        case MetricType.LongGauge:
                            document[_value] = metricPoint.GetGaugeLastValueLong();
                            break;
                        case MetricType.DoubleGauge:
                            document[_value] = metricPoint.GetGaugeLastValueDouble();
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
                return ExportResult.Success;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error exporting metrics: {ex.Message}");
                return ExportResult.Failure;
            }
            finally
            {
                documents.Clear();
            }
        }
    }
}
