using Blocks.Genesis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

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
    public void GetHeader_ShouldReturnNull_WhenHeaderMissingOrHeadersNull()
    {
        var method = typeof(RabbitMessageWorker).GetMethod("GetHeader", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var propsWithoutHeaders = new Mock<IReadOnlyBasicProperties>();
        propsWithoutHeaders.SetupGet(x => x.Headers).Returns((IDictionary<string, object?>?)null);

        var missing = new Mock<IReadOnlyBasicProperties>();
        missing.SetupGet(x => x.Headers).Returns(new Dictionary<string, object?>
        {
            ["Other"] = Encoding.UTF8.GetBytes("value")
        });

        var value1 = (string?)method!.Invoke(null, [propsWithoutHeaders.Object, "TenantId"]);
        var value2 = (string?)method!.Invoke(null, [missing.Object, "TenantId"]);

        Assert.Null(value1);
        Assert.Null(value2);
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

    [Fact]
    public async Task StopAsync_ShouldNotCloseOrDispose_WhenChannelIsClosedOrNull()
    {
        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(false);

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumer(), new MessageConfiguration
        {
            RabbitMqConfiguration = new RabbitMqConfiguration()
        });

        var field = typeof(RabbitMessageWorker).GetField("_channel", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(worker, channel.Object);

        await worker.StopAsync(CancellationToken.None);

        channel.Verify(x => x.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        channel.Verify(x => x.DisposeAsync(), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldThrow_WhenChannelNotInitialized()
    {
        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var rabbitService = new Mock<IRabbitMqService>();
        rabbitService.Setup(x => x.CreateConnectionAsync()).Returns(Task.CompletedTask);
        rabbitService.Setup(x => x.InitializeSubscriptionsAsync()).Returns(Task.CompletedTask);
        rabbitService.SetupGet(x => x.RabbitMqChannel).Returns((IChannel)null!);

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumer(), CreateConfiguration("orders.queue"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeExecuteAsync(worker));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldInitializeAndStartConsuming()
    {
        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel
            .Setup(x => x.BasicConsumeAsync(
                It.IsAny<string>(),
                false,
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>>(),
                It.IsAny<IAsyncBasicConsumer>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("consumer-tag");

        rabbitService.Setup(x => x.CreateConnectionAsync()).Returns(Task.CompletedTask);
        rabbitService.Setup(x => x.InitializeSubscriptionsAsync()).Returns(Task.CompletedTask);
        rabbitService.SetupGet(x => x.RabbitMqChannel).Returns(channel.Object);

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumer(), CreateConfiguration("orders.queue"));

        await InvokeExecuteAsync(worker);

        rabbitService.Verify(x => x.CreateConnectionAsync(), Times.Once);
        rabbitService.Verify(x => x.InitializeSubscriptionsAsync(), Times.Once);
        channel.Verify(x => x.BasicConsumeAsync(
            "orders.queue",
            false,
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<IDictionary<string, object?>>(),
            It.IsAny<IAsyncBasicConsumer>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleMessageAsync_ShouldProcessAndAck_WhenMessageIsValid()
    {
        WorkerConsumerProbe.Reset();

        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel
            .Setup(x => x.BasicAckAsync(It.IsAny<ulong>(), false, It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumerWithProbe(), CreateConfiguration("orders.queue"));
        SetPrivateChannel(worker, channel.Object);

        var payload = JsonSerializer.Serialize(new WorkerPayload { Value = "ok" });
        var envelope = JsonSerializer.Serialize(new global::Blocks.Genesis.Message { Type = nameof(WorkerPayload), Body = payload });
        var ea = CreateEventArgs("orders.queue", envelope, deliveryTag: 42);

        await InvokePrivateAsync(worker, "HandleMessageAsync", new object[] { new object(), ea });

    Assert.Equal("ok", WorkerConsumerProbe.LastValue);
    channel.Verify(x => x.BasicAckAsync(42, false, It.IsAny<CancellationToken>()), Times.Once);
    Assert.Null(BlocksContext.GetContext());
    }

    [Fact]
    public async Task HandleMessageAsync_ShouldAckAndSwallow_WhenMessageIsInvalidJson()
    {
        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel
            .Setup(x => x.BasicAckAsync(It.IsAny<ulong>(), false, It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumer(), CreateConfiguration("orders.queue"));
        SetPrivateChannel(worker, channel.Object);

        var ea = CreateEventArgs("orders.queue", "not-json", deliveryTag: 7);

        var exception = await Record.ExceptionAsync(() => InvokePrivateAsync(worker, "HandleMessageAsync", new object[] { new object(), ea }));

    Assert.Null(exception);
    channel.Verify(x => x.BasicAckAsync(7, false, It.IsAny<CancellationToken>()), Times.Once);
    Assert.Null(BlocksContext.GetContext());
    }

    [Fact]
    public async Task StartConsumingAsync_ShouldNotCallBasicConsume_WhenSubscriptionsEmpty()
    {
        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        var worker = CreateWorker(
            logger.Object,
            rabbitService.Object,
            CreateConsumer(),
            new MessageConfiguration { RabbitMqConfiguration = new RabbitMqConfiguration() });

        SetPrivateChannel(worker, channel.Object);

        await InvokePrivateAsync(worker, "StartConsumingAsync", new object[] { new AsyncEventingBasicConsumer(channel.Object) });

        channel.Verify(x => x.BasicConsumeAsync(
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<bool>(),
            It.IsAny<IDictionary<string, object?>>(),
            It.IsAny<IAsyncBasicConsumer>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static RabbitMessageWorker CreateWorker(
        ILogger<RabbitMessageWorker> logger,
        IRabbitMqService rabbitService,
        Consumer consumer,
        MessageConfiguration configuration)
    {
        return new RabbitMessageWorker(
            logger,
            configuration,
            rabbitService,
            consumer,
            new ActivitySource("test-worker"));
    }

    private static MessageConfiguration CreateConfiguration(string queueName)
    {
        return new MessageConfiguration
        {
            RabbitMqConfiguration = new RabbitMqConfiguration
            {
                ConsumerSubscriptions =
                [
                    ConsumerSubscription.BindToQueue(queueName, 3)
                ]
            }
        };
    }

    private static Consumer CreateConsumer()
    {
        var consumerLogger = new Mock<ILogger<Consumer>>();
        var serviceCollection = new ServiceCollection();
        var routing = new RoutingTable(serviceCollection);
        var serviceProvider = serviceCollection.BuildServiceProvider();
        return new Consumer(consumerLogger.Object, serviceProvider, routing);
    }

    private static Consumer CreateConsumerWithProbe()
    {
        var consumerLogger = new Mock<ILogger<Consumer>>();
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<IConsumer<WorkerPayload>, WorkerConsumerProbe>();

        var routing = new RoutingTable(serviceCollection);
        var serviceProvider = serviceCollection.BuildServiceProvider();
        return new Consumer(consumerLogger.Object, serviceProvider, routing);
    }

    private static void SetPrivateChannel(RabbitMessageWorker worker, IChannel channel)
    {
        var field = typeof(RabbitMessageWorker).GetField("_channel", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(worker, channel);
    }

    private static async Task InvokeExecuteAsync(RabbitMessageWorker worker)
    {
        var method = typeof(RabbitMessageWorker).GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task)method!.Invoke(worker, [CancellationToken.None])!;
        await task;
    }

    private static async Task InvokePrivateAsync(object instance, string methodName, object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task)method!.Invoke(instance, args)!;
        await task;
    }

    private static BasicDeliverEventArgs CreateEventArgs(string routingKey, string body, ulong deliveryTag)
    {
        var ea = (BasicDeliverEventArgs)RuntimeHelpers.GetUninitializedObject(typeof(BasicDeliverEventArgs));

        var properties = new BasicProperties
        {
            Headers = new Dictionary<string, object?>
            {
                ["TenantId"] = Encoding.UTF8.GetBytes("tenant-1"),
                ["TraceId"] = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef"),
                ["SpanId"] = Encoding.UTF8.GetBytes("0123456789abcdef"),
                ["SecurityContext"] = Encoding.UTF8.GetBytes("{}"),
                ["Baggage"] = Encoding.UTF8.GetBytes("{}")
            }
        };

        SetMember(ea, "RoutingKey", routingKey);
        SetMember(ea, "BasicProperties", properties);
        SetMember(ea, "Body", new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(body)));
        SetMember(ea, "DeliveryTag", deliveryTag);

        return ea;
    }

    private static void SetMember(object instance, string name, object value)
    {
        var type = instance.GetType();
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (property?.SetMethod != null)
        {
            property.SetValue(instance, value);
            return;
        }

        var field = type.GetField($"<{name}>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }

    private sealed class WorkerPayload
    {
        public string? Value { get; set; }
    }

    private sealed class WorkerConsumerProbe : IConsumer<WorkerPayload>
    {
        public static string? LastValue { get; private set; }

        public static void Reset() => LastValue = null;

        public Task Consume(WorkerPayload context)
        {
            LastValue = context.Value;
            return Task.CompletedTask;
        }
    }
}
