using Blocks.Genesis;

namespace XUnitTest.Message;

public class MessageConfigurationTests
{
    [Fact]
    public void GetSubscriptionName_ShouldReturnServiceScopedSubscription()
    {
        var config = new MessageConfiguration { ServiceName = "test-service" };

        var subscriptionName = config.GetSubscriptionName("orders");

        Assert.Equal("orders_sub_test-service", subscriptionName);
    }

    [Fact]
    public void AzureServiceBusConfiguration_Queues_ShouldNormalizeAndFilter()
    {
        var config = new AzureServiceBusConfiguration();

        config.Queues = ["QueueOne", "QUEUETWO", "", "   "];

        Assert.Equal(["queueone", "queuetwo"], config.Queues);
    }

    [Fact]
    public void AzureServiceBusConfiguration_Queues_ShouldKeepPreviousValue_WhenSetToNull()
    {
        var config = new AzureServiceBusConfiguration
        {
            Queues = ["queueone"]
        };

        config.Queues = null!;

        Assert.Equal(["queueone"], config.Queues);
    }

    [Fact]
    public void AzureServiceBusConfiguration_Topics_ShouldNormalizeAndFilter()
    {
        var config = new AzureServiceBusConfiguration();

        config.Topics = ["Users", "AUDIT", "", "\t"];

        Assert.Equal(["users", "audit"], config.Topics);
    }

    [Fact]
    public void AzureServiceBusConfiguration_Defaults_ShouldMatchConfiguredValues()
    {
        var config = new AzureServiceBusConfiguration();

        Assert.Equal(1024, config.QueueMaxSizeInMegabytes);
        Assert.Equal(2, config.QueueMaxDeliveryCount);
        Assert.Equal(10, config.QueuePrefetchCount);
        Assert.Equal(TimeSpan.FromDays(7), config.QueueDefaultMessageTimeToLive);
        Assert.Equal(1024, config.TopicMaxSizeInMegabytes);
        Assert.Equal(10, config.TopicPrefetchCount);
        Assert.Equal(TimeSpan.FromDays(30), config.TopicDefaultMessageTimeToLive);
        Assert.Equal(2, config.TopicSubscriptionMaxDeliveryCount);
        Assert.Equal(TimeSpan.FromDays(7), config.TopicSubscriptionDefaultMessageTimeToLive);
        Assert.Equal(5, config.MaxConcurrentCalls);
        Assert.Equal(60, config.MaxMessageProcessingTimeInMinutes);
        Assert.Equal(270, config.MessageLockRenewalIntervalSeconds);
    }

    [Fact]
    public void ConsumerSubscription_FactoryMethods_ShouldPopulateExpectedProperties()
    {
        var queueBinding = ConsumerSubscription.BindToQueue("inbox", 7);
        var exchangeBinding = ConsumerSubscription.BindToQueueViaExchange("inbox", "events", 9, "topic", "order.created", true, false);

        Assert.Equal("inbox", queueBinding.QueueName);
        Assert.Equal(string.Empty, queueBinding.ExchangeName);
        Assert.Equal((ushort)7, queueBinding.PrefetchCount);
        Assert.Equal("fanout", queueBinding.ExchangeType);

        Assert.Equal("inbox", exchangeBinding.QueueName);
        Assert.Equal("events", exchangeBinding.ExchangeName);
        Assert.Equal((ushort)9, exchangeBinding.PrefetchCount);
        Assert.Equal("topic", exchangeBinding.ExchangeType);
        Assert.Equal("order.created", exchangeBinding.RoutingKey);
        Assert.True(exchangeBinding.ShouldBypassAuthorization);
        Assert.False(exchangeBinding.Durable);
    }
}
