using Azure;
using Azure.Messaging.ServiceBus.Administration;
using Blocks.Genesis;
using Moq;
using System.Reflection;

namespace XUnitTest.Message.Azure;

public class ConfigureAzureServiceBusAdditionalTests
{
    [Fact]
    public async Task ConfigureQueueAndTopicAsync_ShouldReturnEarly_WhenConnectionIsMissing()
    {
        // Non-obsolete public entry point. Parallel test to the obsolete alias.
        var configuration = new MessageConfiguration
        {
            Connection = string.Empty,
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration()
        };

        var exception = await Record.ExceptionAsync(() => ConfigureAzureServiceBus.ConfigureQueueAndTopicAsync(configuration));

        Assert.Null(exception);
    }

    [Fact]
    public async Task ObsoleteAlias_ShouldDelegateTo_ConfigureQueueAndTopicAsync()
    {
#pragma warning disable CS0618 // intentionally exercising the obsolete overload
        var configuration = new MessageConfiguration
        {
            Connection = "   ",
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration()
        };

        var exception = await Record.ExceptionAsync(() => ConfigerAzureServiceBus.ConfigerQueueAndTopicAsync(configuration));
#pragma warning restore CS0618

        Assert.Null(exception);
    }

    [Fact]
    public async Task CreateTopicSubscriptionAsync_ShouldCreateWithoutFilter_WhenFilterIsEmpty()
    {
        // Covers the `else` branch at ConfigureAzureServiceBus.cs:153-156
        // (CreateSubscriptionAsync without a CreateRuleOptions filter).
        var admin = new Mock<ServiceBusAdministrationClient>();
        admin.Setup(x => x.SubscriptionExistsAsync("topic-a", "topic-a_sub_svc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(false, Mock.Of<Response>()));
        admin.Setup(x => x.CreateSubscriptionAsync(It.IsAny<CreateSubscriptionOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue<SubscriptionProperties>(null!, Mock.Of<Response>()));

        SetPrivateStaticField("_adminClient", admin.Object);
        SetPrivateStaticField("_messageConfiguration", new MessageConfiguration
        {
            Connection = "Endpoint=sb://x.servicebus.windows.net/;SharedAccessKeyName=n;SharedAccessKey=k",
            ServiceName = "svc",
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration
            {
                Queues = new List<string>(),
                Topics = new List<string> { "topic-a" },
                TopicSubscriptionMaxDeliveryCount = 2
            }
        });

        await InvokePrivateStaticAsync("CreateTopicSubscriptionAsync", "topic-a", "", "", "BlocksRule");

        admin.Verify(
            x => x.CreateSubscriptionAsync(It.IsAny<CreateSubscriptionOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
        admin.Verify(
            x => x.CreateSubscriptionAsync(It.IsAny<CreateSubscriptionOptions>(), It.IsAny<CreateRuleOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static void SetPrivateStaticField(string fieldName, object value)
    {
        var field = typeof(ConfigureAzureServiceBus).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)!;
        field.SetValue(null, value);
    }

    private static async Task InvokePrivateStaticAsync(string methodName, params object[] args)
    {
        var method = typeof(ConfigureAzureServiceBus).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = (Task)method.Invoke(null, args)!;
        await task;
    }
}
