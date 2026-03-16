using Azure;
using Azure.Messaging.ServiceBus.Administration;
using Blocks.Genesis;
using Moq;
using System.Reflection;

namespace XUnitTest.Message.Azure;

public class ConfigerAzureServiceBusTests
{
    [Fact]
    public async Task ConfigerQueueAndTopicAsync_ShouldReturn_WhenConnectionIsMissing()
    {
        var configuration = new MessageConfiguration
        {
            Connection = " ",
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration()
        };

        var exception = await Record.ExceptionAsync(() => ConfigerAzureServiceBus.ConfigerQueueAndTopicAsync(configuration));

        Assert.Null(exception);
    }

    [Fact]
    public async Task CreateQueuesAsync_ShouldCreateOnlyMissingQueues()
    {
        var admin = new Mock<ServiceBusAdministrationClient>();
        admin.Setup(x => x.QueueExistsAsync("q1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(false, Mock.Of<Response>()));
        admin.Setup(x => x.QueueExistsAsync("q2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(true, Mock.Of<Response>()));
        admin.Setup(x => x.CreateQueueAsync(It.IsAny<CreateQueueOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue<QueueProperties>(null!, Mock.Of<Response>()));

        SetPrivateStaticField("_adminClient", admin.Object);
        SetPrivateStaticField("_messageConfiguration", CreateConfiguration(queues: ["q1", "q2"], topics: []));

        await InvokePrivateStaticAsync("CreateQueuesAsync");

        admin.Verify(x => x.CreateQueueAsync(It.IsAny<CreateQueueOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateTopicAsync_ShouldCreateMissingTopics_AndSubscriptions()
    {
        var admin = new Mock<ServiceBusAdministrationClient>();
        admin.Setup(x => x.TopicExistsAsync("t1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(false, Mock.Of<Response>()));
        admin.Setup(x => x.TopicExistsAsync("t2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(true, Mock.Of<Response>()));

        admin.Setup(x => x.CreateTopicAsync(It.IsAny<CreateTopicOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue<TopicProperties>(null!, Mock.Of<Response>()));

        admin.Setup(x => x.SubscriptionExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(false, Mock.Of<Response>()));

        admin.Setup(x => x.CreateSubscriptionAsync(It.IsAny<CreateSubscriptionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue<SubscriptionProperties>(null!, Mock.Of<Response>()));

        SetPrivateStaticField("_adminClient", admin.Object);
        SetPrivateStaticField("_messageConfiguration", CreateConfiguration(queues: [], topics: ["t1", "t2"], serviceName: "svc"));

        await InvokePrivateStaticAsync("CreateTopicAsync");

        admin.Verify(x => x.CreateTopicAsync(It.IsAny<CreateTopicOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        admin.Verify(x => x.CreateSubscriptionAsync(It.IsAny<CreateSubscriptionOptions>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CreateTopicSubscriptionAsync_ShouldUseRuleFilter_WhenFilterProvided()
    {
        var admin = new Mock<ServiceBusAdministrationClient>();
        admin.Setup(x => x.SubscriptionExistsAsync("topic-1", "custom-sub", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(false, Mock.Of<Response>()));

        admin.Setup(x => x.CreateSubscriptionAsync(
                It.IsAny<CreateSubscriptionOptions>(),
                It.IsAny<CreateRuleOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue<SubscriptionProperties>(null!, Mock.Of<Response>()));

        SetPrivateStaticField("_adminClient", admin.Object);
        SetPrivateStaticField("_messageConfiguration", CreateConfiguration(queues: [], topics: ["topic-1"], serviceName: "svc"));

        await InvokePrivateStaticAsync("CreateTopicSubscriptionAsync", "topic-1", "custom-sub", "tenant-1", "rule-1");

        admin.Verify(x => x.CreateSubscriptionAsync(
            It.IsAny<CreateSubscriptionOptions>(),
            It.Is<CreateRuleOptions>(r => r.Name == "rule-1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateTopicSubscriptionAsync_ShouldSkipCreate_WhenSubscriptionExists()
    {
        var admin = new Mock<ServiceBusAdministrationClient>();
        admin.Setup(x => x.SubscriptionExistsAsync("topic-1", "topic-1_sub_svc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(true, Mock.Of<Response>()));

        SetPrivateStaticField("_adminClient", admin.Object);
        SetPrivateStaticField("_messageConfiguration", CreateConfiguration(queues: [], topics: ["topic-1"], serviceName: "svc"));

        await InvokePrivateStaticAsync("CreateTopicSubscriptionAsync", "topic-1", "", "", "BlocksRule");

        admin.Verify(x => x.CreateSubscriptionAsync(It.IsAny<CreateSubscriptionOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        admin.Verify(x => x.CreateSubscriptionAsync(It.IsAny<CreateSubscriptionOptions>(), It.IsAny<CreateRuleOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateQueuesAsync_ShouldHandleEmptyQueueList()
    {
        var admin = new Mock<ServiceBusAdministrationClient>();
        SetPrivateStaticField("_adminClient", admin.Object);
        SetPrivateStaticField("_messageConfiguration", CreateConfiguration(queues: [], topics: []));

        var exception = await Record.ExceptionAsync(() => InvokePrivateStaticAsync("CreateQueuesAsync"));

        Assert.Null(exception);
        admin.Verify(x => x.CreateQueueAsync(It.IsAny<CreateQueueOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateTopicAsync_ShouldHandleEmptyTopicList()
    {
        var admin = new Mock<ServiceBusAdministrationClient>();
        SetPrivateStaticField("_adminClient", admin.Object);
        SetPrivateStaticField("_messageConfiguration", CreateConfiguration(queues: [], topics: []));

        var exception = await Record.ExceptionAsync(() => InvokePrivateStaticAsync("CreateTopicAsync"));

        Assert.Null(exception);
        admin.Verify(x => x.CreateTopicAsync(It.IsAny<CreateTopicOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        admin.Verify(x => x.CreateSubscriptionAsync(It.IsAny<CreateSubscriptionOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateTopicAsync_ShouldRethrow_WhenTopicLookupFails()
    {
        var admin = new Mock<ServiceBusAdministrationClient>();
        admin.Setup(x => x.TopicExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("topic-check-failed"));

        SetPrivateStaticField("_adminClient", admin.Object);
        SetPrivateStaticField("_messageConfiguration", CreateConfiguration(queues: [], topics: ["t1"]));

        var exception = await Record.ExceptionAsync(() => InvokePrivateStaticAsync("CreateTopicAsync"));

        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public async Task CreateTopicSubscriptionAsync_ShouldRethrow_WhenSubscriptionLookupFails()
    {
        var admin = new Mock<ServiceBusAdministrationClient>();
        admin.Setup(x => x.SubscriptionExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("subscription-check-failed"));

        SetPrivateStaticField("_adminClient", admin.Object);
        SetPrivateStaticField("_messageConfiguration", CreateConfiguration(queues: [], topics: ["topic-1"], serviceName: "svc"));

        var exception = await Record.ExceptionAsync(() => InvokePrivateStaticAsync("CreateTopicSubscriptionAsync", "topic-1", "", "", "BlocksRule"));

        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public async Task ConfigerQueueAndTopicAsync_ShouldRethrow_WhenConnectionStringIsInvalid()
    {
        var configuration = new MessageConfiguration
        {
            Connection = "invalid-connection",
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration
            {
                Queues = ["q1"],
                Topics = []
            }
        };

        var exception = await Record.ExceptionAsync(() => ConfigerAzureServiceBus.ConfigerQueueAndTopicAsync(configuration));

        Assert.NotNull(exception);
    }

    [Fact]
    public async Task CheckMethods_ShouldDelegateToAdministrationClient()
    {
        var admin = new Mock<ServiceBusAdministrationClient>();
        admin.Setup(x => x.QueueExistsAsync("q1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(true, Mock.Of<Response>()));
        admin.Setup(x => x.TopicExistsAsync("t1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(false, Mock.Of<Response>()));
        admin.Setup(x => x.SubscriptionExistsAsync("t1", "sub1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(true, Mock.Of<Response>()));

        SetPrivateStaticField("_adminClient", admin.Object);

        var queueExists = await InvokePrivateStaticAsync<bool>("CheckQueueExistsAsync", "q1");
        var topicExists = await InvokePrivateStaticAsync<bool>("CheckTopicExistsAsync", "t1");
        var subscriptionExists = await InvokePrivateStaticAsync<bool>("CheckSubscriptionExistsAsync", "t1", "sub1");

        Assert.True(queueExists);
        Assert.False(topicExists);
        Assert.True(subscriptionExists);
    }

    [Fact]
    public async Task CreateQueuesAsync_ShouldRethrow_WhenAdministrationClientFails()
    {
        var admin = new Mock<ServiceBusAdministrationClient>();
        admin.Setup(x => x.QueueExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("queue-check-failed"));

        SetPrivateStaticField("_adminClient", admin.Object);
        SetPrivateStaticField("_messageConfiguration", CreateConfiguration(queues: ["q1"], topics: []));

        var exception = await Record.ExceptionAsync(() => InvokePrivateStaticAsync("CreateQueuesAsync"));

        Assert.IsType<InvalidOperationException>(exception);
    }

    private static MessageConfiguration CreateConfiguration(IEnumerable<string> queues, IEnumerable<string> topics, string serviceName = "service")
    {
        return new MessageConfiguration
        {
            Connection = "Endpoint=sb://unit-test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=key",
            ServiceName = serviceName,
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration
            {
                Queues = queues.ToList(),
                Topics = topics.ToList(),
                QueueMaxSizeInMegabytes = 1024,
                QueueMaxDeliveryCount = 3,
                TopicMaxSizeInMegabytes = 1024,
                TopicSubscriptionMaxDeliveryCount = 2
            }
        };
    }

    private static void SetPrivateStaticField(string fieldName, object value)
    {
        var field = typeof(ConfigerAzureServiceBus).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        field!.SetValue(null, value);
    }

    private static async Task InvokePrivateStaticAsync(string methodName, params object[] args)
    {
        var method = typeof(ConfigerAzureServiceBus).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var task = (Task)method!.Invoke(null, args)!;
        await task;
    }

    private static async Task<T> InvokePrivateStaticAsync<T>(string methodName, params object[] args)
    {
        var method = typeof(ConfigerAzureServiceBus).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var task = (Task)method!.Invoke(null, args)!;
        await task;

        var resultProperty = task.GetType().GetProperty("Result")!;
        return (T)resultProperty.GetValue(task)!;
    }
}