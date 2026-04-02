using Blocks.Genesis;

namespace XUnitTest.Lmt;

public class LmtConfigurationPropertyTests
{
    [Fact]
    public void LogDatabaseName_ShouldReturnLogs()
    {
        Assert.Equal("Logs", LmtConfiguration.LogDatabaseName);
    }

    [Fact]
    public void TraceDatabaseName_ShouldReturnTraces()
    {
        Assert.Equal("Traces", LmtConfiguration.TraceDatabaseName);
    }

    [Fact]
    public void MetricDatabaseName_ShouldReturnMetrics()
    {
        Assert.Equal("Metrics", LmtConfiguration.MetricDatabaseName);
    }
}
