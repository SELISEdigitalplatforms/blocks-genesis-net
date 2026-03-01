using Blocks.Genesis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System.Reflection;

namespace XUnitTest.Message.Azure;

public class AzureMessageWorkerScaffoldTests
{
    [Fact]
    public void DeserializeBaggage_ShouldReturnDictionary_ForValidJson()
    {
        var method = typeof(AzureMessageWorker).GetMethod("DeserializeBaggage", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = (Dictionary<string, string>)method!.Invoke(null, ["{\"k\":\"v\"}"])!;

        Assert.Single(result);
        Assert.Equal("v", result["k"]);
    }

    [Fact]
    public void DeserializeBaggage_ShouldReturnEmptyDictionary_ForInvalidJson()
    {
        var method = typeof(AzureMessageWorker).GetMethod("DeserializeBaggage", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = (Dictionary<string, string>)method!.Invoke(null, ["{invalid-json"])!;

        Assert.Empty(result);
    }

    [Fact]
    public async Task StopAsync_ShouldNotThrow_WhenNoProcessorsWereStarted()
    {
        var logger = new Mock<ILogger<AzureMessageWorker>>();

        var services = new ServiceCollection().BuildServiceProvider();
        var consumerLogger = new Mock<ILogger<Consumer>>();
        var consumer = new Consumer(consumerLogger.Object, services, new RoutingTable(new ServiceCollection()));

        var config = new MessageConfiguration
        {
            Connection = string.Empty,
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration()
        };

        var worker = new AzureMessageWorker(
            logger.Object,
            config,
            consumer,
            new System.Diagnostics.ActivitySource("test-azure-worker"));

        await worker.StopAsync(CancellationToken.None);
    }
}
