using Blocks.Genesis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using RabbitMQ.Client;
using System.Reflection;
using System.Text;

namespace XUnitTest.Message.RabbitMq;

public class RabbitMessageWorkerScaffoldTests
{
    [Fact]
    public void GetHeader_ShouldReturnDecodedValue_WhenHeaderExists()
    {
        var method = typeof(RabbitMessageWorker).GetMethod("GetHeader", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var props = new Mock<IReadOnlyBasicProperties>();
        props.SetupGet(x => x.Headers).Returns(new Dictionary<string, object?>
        {
            ["TenantId"] = Encoding.UTF8.GetBytes("tenant-1")
        });

        var value = (string?)method!.Invoke(null, [props.Object, "TenantId"]);

        Assert.Equal("tenant-1", value);
    }

    [Fact]
    public void ExtractHeaders_ShouldPopulateOutputValues()
    {
        var method = typeof(RabbitMessageWorker).GetMethod("ExtractHeaders", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var props = new Mock<IReadOnlyBasicProperties>();
        props.SetupGet(x => x.Headers).Returns(new Dictionary<string, object?>
        {
            ["TenantId"] = Encoding.UTF8.GetBytes("tenant-2"),
            ["TraceId"] = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef"),
            ["SpanId"] = Encoding.UTF8.GetBytes("0123456789abcdef"),
            ["SecurityContext"] = Encoding.UTF8.GetBytes("{}"),
            ["Baggage"] = Encoding.UTF8.GetBytes("{\"k\":\"v\"}")
        });

        object?[] args = [props.Object, null, null, null, null, null];
        method!.Invoke(null, args);

        Assert.Equal("tenant-2", args[1]);
        Assert.Equal("0123456789abcdef0123456789abcdef", args[2]);
        Assert.Equal("0123456789abcdef", args[3]);
        Assert.Equal("{}", args[4]);
        Assert.Equal("{\"k\":\"v\"}", args[5]);
    }

    [Fact]
    public async Task StopAsync_ShouldCloseAndDisposeChannel_WhenOpen()
    {
        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);

        var consumerLogger = new Mock<ILogger<Consumer>>();
        var sp = new ServiceCollection().BuildServiceProvider();
        var consumer = new Consumer(consumerLogger.Object, sp, new RoutingTable(new ServiceCollection()));

        var worker = new RabbitMessageWorker(
            logger.Object,
            new MessageConfiguration { RabbitMqConfiguration = new RabbitMqConfiguration() },
            rabbitService.Object,
            consumer,
            new System.Diagnostics.ActivitySource("test-worker"));

        var field = typeof(RabbitMessageWorker).GetField("_channel", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(worker, channel.Object);

        await worker.StopAsync(CancellationToken.None);

        channel.Verify(x => x.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(x => x.DisposeAsync(), Times.Once);
    }
}
