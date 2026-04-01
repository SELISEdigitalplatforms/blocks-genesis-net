using SeliseBlocks.LMT.Client;

namespace XUnitTest.Lmt;

public class LmtConstantsTests
{
    [Fact]
    public void LogSubscription_ShouldHaveExpectedValue()
    {
        Assert.Equal("blocks-lmt-service-logs", LmtConstants.LogSubscription);
    }

    [Fact]
    public void TraceSubscription_ShouldHaveExpectedValue()
    {
        Assert.Equal("blocks-lmt-service-traces", LmtConstants.TraceSubscription);
    }

    [Fact]
    public void RabbitMqLogsRoutingKey_ShouldHaveExpectedValue()
    {
        Assert.Equal("logs", LmtConstants.RabbitMqLogsRoutingKey);
    }

    [Fact]
    public void RabbitMqTracesRoutingKey_ShouldHaveExpectedValue()
    {
        Assert.Equal("traces", LmtConstants.RabbitMqTracesRoutingKey);
    }

    [Fact]
    public void GetTopicName_ShouldReturnPrefixedName()
    {
        Assert.Equal("lmt-my-service", LmtConstants.GetTopicName("my-service"));
    }

    [Fact]
    public void GetRabbitMqExchangeName_ShouldReturnPrefixedName()
    {
        Assert.Equal("lmt-my-service", LmtConstants.GetRabbitMqExchangeName("my-service"));
    }
}
