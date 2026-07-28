using Blocks.Genesis;

namespace XUnitTest.Message;

public class MessageConfigurationCoverageTests
{
    [Fact]
    public void Queues_Setter_ShouldKeepExistingList_WhenValueIsNull()
    {
        var configuration = new AzureServiceBusConfiguration
        {
            Queues = ["Queue-A"]
        };

        configuration.Queues = null!;

        Assert.Equal(new List<string> { "queue-a" }, configuration.Queues);
    }

    [Fact]
    public void Topics_Setter_ShouldKeepExistingList_WhenValueIsNull()
    {
        var configuration = new AzureServiceBusConfiguration
        {
            Topics = ["Topic-A"]
        };

        configuration.Topics = null!;

        Assert.Equal(new List<string> { "topic-a" }, configuration.Topics);
    }

    [Fact]
    public void BindToQueueViaExchange_ShouldSetParallelProcessing()
    {
        Assert.True(ConsumerSubscription.BindToQueueViaExchange("queue-a", "exchange-a", 5, parallelProcessing: true).ParallelProcessing);
        Assert.False(ConsumerSubscription.BindToQueueViaExchange("queue-a", "exchange-a").ParallelProcessing);
    }

    [Fact]
    public void BindToQueueViaExchange_Detailed_ShouldSetParallelProcessing()
    {
        var subscription = ConsumerSubscription.BindToQueueViaExchange(
            "queue-a",
            "exchange-a",
            5,
            "direct",
            "routing-key",
            parallelProcessing: true);

        Assert.True(subscription.ParallelProcessing);
        Assert.Equal("direct", subscription.ExchangeType);
        Assert.Equal("routing-key", subscription.RoutingKey);
    }
}
