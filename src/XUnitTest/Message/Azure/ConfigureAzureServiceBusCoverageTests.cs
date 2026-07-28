using Azure;
using Azure.Messaging.ServiceBus.Administration;
using Blocks.Genesis;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Reflection;

namespace XUnitTest.Message.Azure;

// ConfigureAzureServiceBus keeps its administration client and configuration in static fields.
// Joining the implicit collection of ConfigerAzureServiceBusTests serializes both classes so
// they never mutate that shared static state concurrently.
[Collection("Test collection for XUnitTest.Message.Azure.ConfigerAzureServiceBusTests")]
public class ConfigureAzureServiceBusCoverageTests
{
    private const string ValidConnection = "Endpoint=sb://unit-test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=key";

    [Fact]
    public async Task ConfigerQueueAndTopicAsync_ShouldDelegate_WhenConnectionIsMissing()
    {
        var configuration = new MessageConfiguration
        {
            Connection = " ",
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration()
        };

#pragma warning disable CS0618
        var exception = await Record.ExceptionAsync(() => ConfigureAzureServiceBus.ConfigerQueueAndTopicAsync(configuration));
#pragma warning restore CS0618

        Assert.Null(exception);
    }

    [Fact]
    public async Task ConfigureQueueAndTopicAsync_ShouldLogWithProvidedLogger_WhenConnectionIsMissing()
    {
        var configuration = new MessageConfiguration
        {
            Connection = string.Empty,
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration()
        };

        var exception = await Record.ExceptionAsync(() => ConfigureAzureServiceBus.ConfigureQueueAndTopicAsync(configuration, NullLogger.Instance));

        Assert.Null(exception);
    }

    [Fact]
    public async Task ConfigureQueueAndTopicAsync_ShouldProvision_WhenConnectionFormatIsValid()
    {
        var configuration = new MessageConfiguration
        {
            Connection = ValidConnection,
            ServiceName = "svc",
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration
            {
                Queues = [],
                Topics = []
            }
        };

        var withLogger = await Record.ExceptionAsync(() => ConfigureAzureServiceBus.ConfigureQueueAndTopicAsync(configuration, NullLogger.Instance));
        var withoutLogger = await Record.ExceptionAsync(() => ConfigureAzureServiceBus.ConfigureQueueAndTopicAsync(configuration));

        Assert.Null(withLogger);
        Assert.Null(withoutLogger);
    }

    [Fact]
    public async Task CreateQueuesAsync_ShouldFallBackToDefaults_WhenConfigurationDisappearsMidRun()
    {
        var admin = new Mock<ServiceBusAdministrationClient>();
        var configuration = CreateConfiguration(queues: ["q1", "q2", "q3"], topics: []);
        SetPrivateStaticField("_adminClient", admin.Object);
        SetPrivateStaticField("_messageConfiguration", configuration);

        admin
            .Setup(x => x.QueueExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((queue, _) =>
            {
                if (queue == "q2")
                {
                    configuration.AzureServiceBusConfiguration = null;
                }
                else if (queue == "q3")
                {
                    SetPrivateStaticField("_messageConfiguration", null);
                }
            })
            .ReturnsAsync(Response.FromValue(false, Mock.Of<Response>()));
        admin
            .Setup(x => x.CreateQueueAsync(It.IsAny<CreateQueueOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue<QueueProperties>(null!, Mock.Of<Response>()));

        await InvokePrivateStaticAsync("CreateQueuesAsync");

        admin.Verify(x => x.CreateQueueAsync(It.IsAny<CreateQueueOptions>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task CreateQueuesAsync_ShouldSkip_WhenConfigurationIsMissing()
    {
        var admin = new Mock<ServiceBusAdministrationClient>();
        SetPrivateStaticField("_adminClient", admin.Object);

        SetPrivateStaticField("_messageConfiguration", null);
        await InvokePrivateStaticAsync("CreateQueuesAsync");

        SetPrivateStaticField("_messageConfiguration", new MessageConfiguration
        {
            Connection = ValidConnection,
            AzureServiceBusConfiguration = null
        });
        await InvokePrivateStaticAsync("CreateQueuesAsync");

        admin.Verify(x => x.CreateQueueAsync(It.IsAny<CreateQueueOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateTopicAsync_ShouldFallBackToDefaults_WhenConfigurationDisappearsMidRun()
    {
        var admin = new Mock<ServiceBusAdministrationClient>();
        var configuration = CreateConfiguration(queues: [], topics: ["t1", "t2", "t3"]);
        SetPrivateStaticField("_adminClient", admin.Object);
        SetPrivateStaticField("_messageConfiguration", configuration);

        admin
            .Setup(x => x.TopicExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((topic, _) =>
            {
                if (topic == "t2")
                {
                    configuration.AzureServiceBusConfiguration = null;
                }
                else if (topic == "t3")
                {
                    SetPrivateStaticField("_messageConfiguration", null);
                }
            })
            .ReturnsAsync(Response.FromValue(false, Mock.Of<Response>()));
        admin
            .Setup(x => x.CreateTopicAsync(It.IsAny<CreateTopicOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue<TopicProperties>(null!, Mock.Of<Response>()));

        await InvokePrivateStaticAsync("CreateTopicAsync");

        admin.Verify(x => x.CreateTopicAsync(It.IsAny<CreateTopicOptions>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        admin.Verify(x => x.CreateSubscriptionAsync(It.IsAny<CreateSubscriptionOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateTopicAsync_ShouldSkip_WhenConfigurationIsMissing()
    {
        var admin = new Mock<ServiceBusAdministrationClient>();
        SetPrivateStaticField("_adminClient", admin.Object);

        SetPrivateStaticField("_messageConfiguration", null);
        await InvokePrivateStaticAsync("CreateTopicAsync");

        SetPrivateStaticField("_messageConfiguration", new MessageConfiguration
        {
            Connection = ValidConnection,
            AzureServiceBusConfiguration = null
        });
        await InvokePrivateStaticAsync("CreateTopicAsync");

        admin.Verify(x => x.CreateTopicAsync(It.IsAny<CreateTopicOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        admin.Verify(x => x.CreateSubscriptionAsync(It.IsAny<CreateSubscriptionOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateTopicSubscriptionAsync_ShouldUseDefaults_WhenConfigurationIsMissing()
    {
        var admin = new Mock<ServiceBusAdministrationClient>();
        admin
            .Setup(x => x.SubscriptionExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(false, Mock.Of<Response>()));
        admin
            .Setup(x => x.CreateSubscriptionAsync(It.IsAny<CreateSubscriptionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue<SubscriptionProperties>(null!, Mock.Of<Response>()));

        SetPrivateStaticField("_adminClient", admin.Object);

        SetPrivateStaticField("_messageConfiguration", new MessageConfiguration
        {
            Connection = ValidConnection,
            AzureServiceBusConfiguration = null
        });
        await InvokePrivateStaticAsync("CreateTopicSubscriptionAsync", "topic-1", "sub-a", "", "BlocksRule");

        SetPrivateStaticField("_messageConfiguration", null);
        await InvokePrivateStaticAsync("CreateTopicSubscriptionAsync", "topic-1", "sub-b", "", "BlocksRule");

        admin.Verify(x => x.CreateSubscriptionAsync(It.IsAny<CreateSubscriptionOptions>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private static MessageConfiguration CreateConfiguration(IEnumerable<string> queues, IEnumerable<string> topics)
    {
        return new MessageConfiguration
        {
            Connection = ValidConnection,
            ServiceName = "svc",
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration
            {
                Queues = queues.ToList(),
                Topics = topics.ToList()
            }
        };
    }

    private static void SetPrivateStaticField(string fieldName, object? value)
    {
        var field = typeof(ConfigureAzureServiceBus).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        field!.SetValue(null, value);
    }

    private static async Task InvokePrivateStaticAsync(string methodName, params object[] args)
    {
        var method = typeof(ConfigureAzureServiceBus).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var task = (Task)method!.Invoke(null, args)!;
        await task;
    }
}
