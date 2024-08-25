using System;
using System.Collections.Generic;
using Xunit;
using Blocks.Genesis;

public class MessageConfigurationTests
{

    [Fact]
    public void Queues_ShouldNormalizeAndFilterEmptyStrings()
    {
        // Arrange
        var config = new MessageConfiguration
        {
            Connection = "TestConnection",
            ServiceName = "TestService",
            Queues = new List<string> { "Queue1", "QUEUE2 ", "  ", null }
        };

        // Act
        var queues = config.Queues;

        // Assert
        Assert.Equal(2, queues.Count);
        Assert.Contains("queue1", queues);
    }

    [Fact]
    public void Topics_ShouldNormalizeAndFilterEmptyStrings()
    {
        // Arrange
        var config = new MessageConfiguration
        {
            Connection = "TestConnection",
            ServiceName = "TestService",
            Topics = new List<string> { "Topic1", "TOPIC2 ", "  ", null }
        };

        // Act
        var topics = config.Topics;

        // Assert
        Assert.Equal(2, topics.Count);
        Assert.Contains("topic1", topics);
    }

    [Fact]
    public void DefaultValues_ShouldBeCorrectlySet()
    {
        // Arrange
        var config = new MessageConfiguration
        {
            Connection = "TestConnection",
            ServiceName = "TestService"
        };

        // Assert
        Assert.Equal(1024, config.QueueMaxSizeInMegabytes);
        Assert.Equal(5, config.QueueMaxDeliveryCount);
        Assert.Equal(5, config.QueuePrefetchCount);
        Assert.Equal(TimeSpan.FromMinutes(60 * 24 * 7), config.QueueDefaultMessageTimeToLive);

        Assert.Equal(1024, config.TopicMaxSizeInMegabytes);
        Assert.Equal(5, config.TopicPrefetchCount);
        Assert.Equal(TimeSpan.FromMinutes(60 * 24 * 30), config.TopicDefaultMessageTimeToLive);

        Assert.Equal(5, config.TopicSubscriptionMaxDeliveryCount);
        Assert.Equal(TimeSpan.FromMinutes(60 * 24 * 7), config.TopicSubscriptionDefaultMessageTimeToLive);
    }

    [Fact]
    public void GetSubscriptionName_ShouldReturnCorrectSubscriptionName()
    {
        // Arrange
        var config = new MessageConfiguration
        {
            Connection = "TestConnection",
            ServiceName = "TestService"
        };

        // Act
        var subscriptionName = config.GetSubscriptionName("topic1");

        // Assert
        Assert.Equal("topic1_sub_TestService", subscriptionName);
    }
}
