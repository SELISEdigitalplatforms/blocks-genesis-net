using Azure.Messaging.ServiceBus;
using Blocks.Genesis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace XUnitTest.Message.Azure;

public class AzureMessageWorkerMoreTests
{
    [Fact]
    public async Task StopAsync_ShouldCatch_WhenProcessorStopProcessingThrows()
    {
        // An uninitialized ServiceBusProcessor throws ArgumentNullException from
        // StopProcessingAsync. The `try/catch` around StopProcessingAsync in
        // AzureMessageWorker.StopAsync (L63-70) must catch it. The `finally` block
        // then calls DisposeAsync on the same uninitialized processor, which also
        // throws — and since it isn't wrapped in try/catch, it propagates. That's
        // the documented behavior; this test confirms the catch branch executed
        // (logger received the StopProcessorFailed event) even though the final
        // dispose still fails.
        var processor = (ServiceBusProcessor)RuntimeHelpers.GetUninitializedObject(typeof(ServiceBusProcessor));

        var logger = new Mock<ILogger<AzureMessageWorker>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var worker = CreateWorkerWithLogger(logger);
        var processors = (List<ServiceBusProcessor>)typeof(AzureMessageWorker)
            .GetField("_processors", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(worker)!;
        processors.Add(processor);

        // DisposeAsync on uninitialized processor will throw — accept that.
        await Assert.ThrowsAnyAsync<Exception>(() => worker.StopAsync(CancellationToken.None));

        // Most importantly: the StopProcessorFailed log (EventId 3004) must have
        // been emitted, proving the catch branch executed.
        logger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.Is<EventId>(e => e.Id == 3004),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    private static AzureMessageWorker CreateWorkerWithLogger(Mock<ILogger<AzureMessageWorker>> logger)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var consumer = new Consumer(Mock.Of<ILogger<Consumer>>(), services, new RoutingTable(new ServiceCollection()));

        return new AzureMessageWorker(
            logger.Object,
            new MessageConfiguration
            {
                Connection = string.Empty,
                AzureServiceBusConfiguration = new AzureServiceBusConfiguration()
            },
            consumer,
            new ActivitySource("test-azure-worker-more"));
    }

    private static AzureMessageWorker CreateWorker()
    {
        var logger = new Mock<ILogger<AzureMessageWorker>>();
        var services = new ServiceCollection().BuildServiceProvider();
        var consumer = new Consumer(Mock.Of<ILogger<Consumer>>(), services, new RoutingTable(new ServiceCollection()));

        return new AzureMessageWorker(
            logger.Object,
            new MessageConfiguration
            {
                Connection = string.Empty,
                AzureServiceBusConfiguration = new AzureServiceBusConfiguration()
            },
            consumer,
            new ActivitySource("test-azure-worker-more"));
    }
}
