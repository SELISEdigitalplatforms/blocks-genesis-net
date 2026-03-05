using SeliseBlocks.LMT.Client;

namespace XUnitTest.Lmt;

public class LmtOptionsTests
{
    [Fact]
    public void LmtOptions_ShouldHaveExpectedDefaults()
    {
        var options = new LmtOptions();

        Assert.Equal(string.Empty, options.ServiceId);
        Assert.Equal(string.Empty, options.ConnectionString);
        Assert.Equal(100, options.LogBatchSize);
        Assert.Equal(1000, options.TraceBatchSize);
        Assert.Equal(5, options.FlushIntervalSeconds);
        Assert.Equal(3, options.MaxRetries);
        Assert.Equal(100, options.MaxFailedBatches);
        Assert.True(options.EnableLogging);
        Assert.True(options.EnableTracing);
        Assert.Equal(string.Empty, options.XBlocksKey);
    }

    [Fact]
    public void LmtOptions_ShouldAllowSettingAllProperties()
    {
        var options = new LmtOptions
        {
            ServiceId = "svc-1",
            ConnectionString = "Endpoint=sb://servicebus/",
            LogBatchSize = 11,
            TraceBatchSize = 22,
            FlushIntervalSeconds = 33,
            MaxRetries = 4,
            MaxFailedBatches = 55,
            EnableLogging = false,
            EnableTracing = false,
            XBlocksKey = "tenant-x"
        };

        Assert.Equal("svc-1", options.ServiceId);
        Assert.Equal("Endpoint=sb://servicebus/", options.ConnectionString);
        Assert.Equal(11, options.LogBatchSize);
        Assert.Equal(22, options.TraceBatchSize);
        Assert.Equal(33, options.FlushIntervalSeconds);
        Assert.Equal(4, options.MaxRetries);
        Assert.Equal(55, options.MaxFailedBatches);
        Assert.False(options.EnableLogging);
        Assert.False(options.EnableTracing);
        Assert.Equal("tenant-x", options.XBlocksKey);
    }

    [Fact]
    public void LmtConstants_ShouldExposeExpectedSubscriptionNames()
    {
        Assert.Equal("blocks-lmt-service-logs", LmtConstants.LogSubscription);
        Assert.Equal("blocks-lmt-service-traces", LmtConstants.TraceSubscription);
    }

    [Fact]
    public void GetTopicName_ShouldPrefixServiceName()
    {
        var topic = LmtConstants.GetTopicName("orders");

        Assert.Equal("lmt-orders", topic);
    }

    [Fact]
    public void GetTopicName_ShouldReturnPrefix_WhenServiceNameIsEmpty()
    {
        var topic = LmtConstants.GetTopicName(string.Empty);

        Assert.Equal("lmt-", topic);
    }
}