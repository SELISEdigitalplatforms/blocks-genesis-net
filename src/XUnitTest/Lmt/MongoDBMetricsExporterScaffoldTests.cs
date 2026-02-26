using Blocks.Genesis;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Moq;

namespace XUnitTest.Lmt;

public class MongoDBMetricsExporterScaffoldTests
{
    [Fact]
    public void Type_ShouldBeAccessible()
    {
        Assert.NotNull(typeof(MongoDBMetricsExporter));
    }

    [Fact]
    public void Constructor_ShouldCreateExporterInstance_WithValidDependencies()
    {
        var blocksSecret = new Mock<IBlocksSecret>();
        blocksSecret.SetupGet(x => x.MetricConnectionString).Returns("mongodb://localhost:27017");

        var exporter = new MongoDBMetricsExporter("svc-metrics", blocksSecret.Object);

        Assert.NotNull(exporter);
    }

    [Fact]
    public void Type_ShouldInheritBaseExporterOfMetric()
    {
        var blocksSecret = new Mock<IBlocksSecret>();
        blocksSecret.SetupGet(x => x.MetricConnectionString).Returns("mongodb://localhost:27017");

        var exporter = new MongoDBMetricsExporter("svc-metrics", blocksSecret.Object);

        Assert.IsAssignableFrom<BaseExporter<Metric>>(exporter);
    }

    [Fact]
    public void Export_ShouldReturnSuccess_ForEmptyBatch()
    {
        var blocksSecret = new Mock<IBlocksSecret>();
        blocksSecret.SetupGet(x => x.MetricConnectionString).Returns("mongodb://localhost:27017");

        var exporter = new MongoDBMetricsExporter("svc-metrics", blocksSecret.Object);
        var emptyBatch = default(Batch<Metric>);

        var result = exporter.Export(in emptyBatch);

        Assert.Equal(ExportResult.Success, result);
    }
}
