using Blocks.Genesis;
using Azure.Messaging.ServiceBus;
using OpenTelemetry;
using System.Collections.Concurrent;
using System.Reflection;

namespace XUnitTest.Message.Azure;

public class AzureMessageClientScaffoldTests
{
    [Fact]
    public void Constructor_ShouldInitializeSenders_ForConfiguredQueuesAndTopics()
    {
        var configuration = new MessageConfiguration
        {
            Connection = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=01234567890123456789012345678901234567890123456789=",
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration
            {
                Queues = ["queue-a"],
                Topics = ["topic-a"]
            }
        };

        var client = new AzureMessageClient(configuration, new System.Diagnostics.ActivitySource("test-azure-client"));

        var field = typeof(AzureMessageClient).GetField("_senders", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        var senders = (ConcurrentDictionary<string, ServiceBusSender>)field!.GetValue(client)!;
        Assert.Equal(2, senders.Count);
        Assert.True(senders.ContainsKey("queue-a"));
        Assert.True(senders.ContainsKey("topic-a"));
    }

    [Fact]
    public void GetBaggageDictionary_ShouldReturnCurrentBaggageItems()
    {
        Baggage.SetBaggage("tenant", "t-1");
        Baggage.SetBaggage("user", "u-1");

        try
        {
            var method = typeof(AzureMessageClient).GetMethod("GetBaggageDictionary", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var result = (Dictionary<string, string>)method!.Invoke(null, null)!;

            Assert.Equal("t-1", result["tenant"]);
            Assert.Equal("u-1", result["user"]);
        }
        finally
        {
            Baggage.SetBaggage("tenant", null);
            Baggage.SetBaggage("user", null);
        }
    }
}
