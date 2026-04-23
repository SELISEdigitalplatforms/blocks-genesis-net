using Blocks.Genesis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System.Diagnostics;
using System.Reflection;

namespace XUnitTest.Message.Azure;

public class AzureMessageWorkerAdditionalTests
{
    private const string ValidConnection =
        "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;" +
        "SharedAccessKey=01234567890123456789012345678901234567890123456789=";

    [Fact]
    public async Task ExecuteAsync_ShouldCatchExceptions_WhenProcessorsFailToStart()
    {
        // Valid-format connection → Service Bus client is constructed successfully,
        // so ExecuteAsync proceeds into ProcessQueues / ProcessTopics. StartProcessingAsync
        // will fail (no real broker), and the outer try/catch in ExecuteAsync swallows it.
        var config = new MessageConfiguration
        {
            Connection = ValidConnection,
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration
            {
                Queues = ["queue-additional"],
                Topics = ["topic-additional"],
                QueuePrefetchCount = 2,
                TopicPrefetchCount = 2,
                MaxConcurrentCalls = 1
            }
        };

        var worker = CreateWorker(config);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var exception = await Record.ExceptionAsync(() => InvokeExecuteAsync(worker, cts.Token));

        // Outer try/catch must have swallowed any StartProcessingAsync failure.
        Assert.Null(exception);
    }

    private static AzureMessageWorker CreateWorker(MessageConfiguration configuration)
    {
        var logger = new Mock<ILogger<AzureMessageWorker>>();
        var consumerLogger = new Mock<ILogger<Consumer>>();
        var sp = new ServiceCollection().BuildServiceProvider();
        var consumer = new Consumer(consumerLogger.Object, sp, new RoutingTable(new ServiceCollection()));

        return new AzureMessageWorker(
            logger.Object,
            configuration,
            consumer,
            new ActivitySource("test-azure-worker-additional"));
    }

    private static async Task InvokeExecuteAsync(AzureMessageWorker worker, CancellationToken token)
    {
        var method = typeof(AzureMessageWorker).GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(worker, [token])!;
        await task;
    }
}
