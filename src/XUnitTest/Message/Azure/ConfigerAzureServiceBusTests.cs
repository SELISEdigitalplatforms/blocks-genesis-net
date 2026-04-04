using Azure;
using Azure.Messaging.ServiceBus.Administration;
using Blocks.Genesis;
using Moq;
using System.Reflection;

namespace XUnitTest.Message.Azure;

public class ConfigerAzureServiceBusTests
{
    [Fact]
    public async Task ConfigerQueueAndTopicAsync_ShouldThrow_WhenConnectionIsMissing()
    {
        var configuration = new MessageConfiguration
        {
            Connection = " ",
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration()
        };

        var exception = await Record.ExceptionAsync(() => ConfigerAzureServiceBus.ConfigerQueueAndTopicAsync(configuration));

        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public async Task ConfigerQueueAndTopicAsync_ShouldThrow_WhenConnectionStringIsInvalid()
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
    public async Task CreateQueuesAsync_ShouldHandleEmptyQueueList()
    {
        var admin = new Mock<ServiceBusAdministrationClient>();
        var config = CreateConfiguration(queues: [], topics: []);

        var exception = await Record.ExceptionAsync(() => InvokePrivateStaticAsync("CreateQueuesAsync", admin.Object, config));

        Assert.Null(exception);
        admin.Verify(x => x.CreateQueueAsync(It.IsAny<CreateQueueOptions>(), It.IsAny<CancellationToken>()), Times.Never);
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

        var config = CreateConfiguration(queues: [], topics: ["topic-1"], serviceName: "svc");

        await InvokePrivateStaticAsync("CreateTopicSubscriptionAsync", admin.Object, config, "topic-1", "custom-sub", "tenant-1", "rule-1");

        admin.Verify(x => x.CreateSubscriptionAsync(
            It.IsAny<CreateSubscriptionOptions>(),
            It.Is<CreateRuleOptions>(r => r.Name == "rule-1"),
            It.IsAny<CancellationToken>()), Times.Once);
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

        var queueExists = await InvokePrivateStaticAsync<bool>("CheckQueueExistsAsync", admin.Object, "q1");
        var topicExists = await InvokePrivateStaticAsync<bool>("CheckTopicExistsAsync", admin.Object, "t1");
        var subscriptionExists = await InvokePrivateStaticAsync<bool>("CheckSubscriptionExistsAsync", admin.Object, "t1", "sub1");

        Assert.True(queueExists);
        Assert.False(topicExists);
        Assert.True(subscriptionExists);
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

    private static async Task InvokePrivateStaticAsync(string methodName, params object[] args)
    {
        var method = ResolvePrivateStaticMethod(methodName, args);
        var task = (Task)method.Invoke(null, args)!;
        await task;
    }

    private static async Task<T> InvokePrivateStaticAsync<T>(string methodName, params object[] args)
    {
        var method = ResolvePrivateStaticMethod(methodName, args);

        var task = (Task)method.Invoke(null, args)!;
        await task;

        var resultProperty = task.GetType().GetProperty("Result")!;
        return (T)resultProperty.GetValue(task)!;
    }

    private static MethodInfo ResolvePrivateStaticMethod(string methodName, object[] args)
    {
        var argTypes = args.Select(a => a?.GetType()).ToArray();

        var method = typeof(ConfigerAzureServiceBus)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(m =>
            {
                if (!string.Equals(m.Name, methodName, StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = m.GetParameters();
                if (parameters.Length != argTypes.Length)
                {
                    return false;
                }

                for (var i = 0; i < parameters.Length; i++)
                {
                    if (argTypes[i] == null)
                    {
                        continue;
                    }

                    if (!parameters[i].ParameterType.IsAssignableFrom(argTypes[i]!))
                    {
                        return false;
                    }
                }

                return true;
            });

        Assert.NotNull(method);
        return method!;
    }
}
