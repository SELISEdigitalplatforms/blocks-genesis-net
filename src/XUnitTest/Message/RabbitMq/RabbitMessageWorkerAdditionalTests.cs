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

public class RabbitMessageWorkerAdditionalTests
{
    [Fact]
    public async Task HandleMessageAsync_ShouldRunOnBackgroundTask_WhenParallelProcessingEnabled()
    {
        ParallelConsumerProbe.Reset();

        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel
            .Setup(x => x.BasicAckAsync(It.IsAny<ulong>(), false, It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var config = new MessageConfiguration
        {
            RabbitMqConfiguration = new RabbitMqConfiguration
            {
                ConsumerSubscriptions =
                [
                    new ConsumerSubscription(
                        queueName: "parallel.queue",
                        exchangeName: string.Empty,
                        prefetchCount: 3,
                        parallelProcessing: true)
                ]
            }
        };

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumerWithProbe(), config);
        SetPrivateChannel(worker, channel.Object);

        var payload = JsonSerializer.Serialize(new ParallelPayload { Value = "parallel-ok" });
        var envelope = JsonSerializer.Serialize(new global::Blocks.Genesis.Message { Type = nameof(ParallelPayload), Body = payload });
        var ea = CreateEventArgs("parallel.queue", envelope, deliveryTag: 101);

        await InvokePrivateAsync(worker, "HandleMessageAsync", new object[] { new object(), ea });

        // Fire-and-forget Task.Run — wait for the probe to observe the value.
        var observed = await WaitForAsync(() => ParallelConsumerProbe.LastValue == "parallel-ok", TimeSpan.FromSeconds(2));
        Assert.True(observed);
        channel.Verify(x => x.BasicAckAsync(101, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessMessageInternalAsync_ShouldAck_WhenMessageEnvelopeDeserializesToNull()
    {
        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel
            .Setup(x => x.BasicAckAsync(It.IsAny<ulong>(), false, It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumer(),
            CreateConfiguration("orders.queue"));
        SetPrivateChannel(worker, channel.Object);

        // JSON `null` deserializes to a null Message envelope → exercises the
        // "empty or invalid message envelope" branch which still acks.
        var ea = CreateEventArgs("orders.queue", "null", deliveryTag: 202);

        var exception = await Record.ExceptionAsync(() => InvokePrivateAsync(worker, "ProcessMessageInternalAsync", new object[] { ea }));

        Assert.Null(exception);
        channel.Verify(x => x.BasicAckAsync(202, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessMessageInternalAsync_ShouldAck_WhenConsumerSwallowsException()
    {
        // Consumer.ProcessMessageAsync catches and logs all exceptions, so a
        // throwing handler does not surface to RabbitMessageWorker. The envelope
        // is therefore acknowledged as successfully processed. This documents
        // that contract and exercises the consumer-invocation path.
        ThrowingConsumerProbe.Reset();

        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel
            .Setup(x => x.BasicAckAsync(It.IsAny<ulong>(), false, It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateThrowingConsumer(),
            CreateConfiguration("orders.queue"));
        SetPrivateChannel(worker, channel.Object);

        var payload = JsonSerializer.Serialize(new ThrowingPayload { Value = "boom" });
        var envelope = JsonSerializer.Serialize(new global::Blocks.Genesis.Message { Type = nameof(ThrowingPayload), Body = payload });
        var ea = CreateEventArgs("orders.queue", envelope, deliveryTag: 303);

        await InvokePrivateAsync(worker, "ProcessMessageInternalAsync", new object[] { ea });

        Assert.True(ThrowingConsumerProbe.WasInvoked);
        channel.Verify(x => x.BasicAckAsync(303, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static RabbitMessageWorker CreateWorker(
        ILogger<RabbitMessageWorker> logger,
        IRabbitMqService rabbitService,
        Consumer consumer,
        MessageConfiguration configuration) =>
        new(logger, configuration, rabbitService, consumer, new ActivitySource("test-worker-additional"));

    private static MessageConfiguration CreateConfiguration(string queueName) => new()
    {
        RabbitMqConfiguration = new RabbitMqConfiguration
        {
            ConsumerSubscriptions = [ConsumerSubscription.BindToQueue(queueName, 3)]
        }
    };

    private static Consumer CreateConsumer()
    {
        var consumerLogger = new Mock<ILogger<Consumer>>();
        var serviceCollection = new ServiceCollection();
        var routing = new RoutingTable(serviceCollection);
        return new Consumer(consumerLogger.Object, serviceCollection.BuildServiceProvider(), routing);
    }

    private static Consumer CreateConsumerWithProbe()
    {
        var consumerLogger = new Mock<ILogger<Consumer>>();
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<IConsumer<ParallelPayload>, ParallelConsumerProbe>();
        var routing = new RoutingTable(serviceCollection);
        return new Consumer(consumerLogger.Object, serviceCollection.BuildServiceProvider(), routing);
    }

    private static Consumer CreateThrowingConsumer()
    {
        var consumerLogger = new Mock<ILogger<Consumer>>();
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<IConsumer<ThrowingPayload>, ThrowingConsumerProbe>();
        var routing = new RoutingTable(serviceCollection);
        return new Consumer(consumerLogger.Object, serviceCollection.BuildServiceProvider(), routing);
    }

    private static void SetPrivateChannel(RabbitMessageWorker worker, IChannel channel)
    {
        var field = typeof(RabbitMessageWorker).GetField("_channel", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(worker, channel);
    }

    private static async Task InvokePrivateAsync(object instance, string methodName, object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)method.Invoke(instance, args)!;
        await task;
    }

    private static BasicDeliverEventArgs CreateEventArgs(string routingKey, string body, ulong deliveryTag)
    {
        var ea = (BasicDeliverEventArgs)RuntimeHelpers.GetUninitializedObject(typeof(BasicDeliverEventArgs));

        var properties = new BasicProperties
        {
            Headers = new Dictionary<string, object?>
            {
                ["TenantId"] = Encoding.UTF8.GetBytes("tenant-additional"),
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
        field!.SetValue(instance, value);
    }

    private static async Task<bool> WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (predicate()) return true;
            await Task.Delay(20, CancellationToken.None);
        }
        return predicate();
    }

    private sealed class ParallelPayload
    {
        public string? Value { get; set; }
    }

    private sealed class ParallelConsumerProbe : IConsumer<ParallelPayload>
    {
        public static volatile string? LastValue;
        public static void Reset() => LastValue = null;

        public Task Consume(ParallelPayload context)
        {
            LastValue = context.Value;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingPayload
    {
        public string? Value { get; set; }
    }

    private sealed class ThrowingConsumerProbe : IConsumer<ThrowingPayload>
    {
        public static volatile bool WasInvoked;
        public static void Reset() => WasInvoked = false;

        public Task Consume(ThrowingPayload context)
        {
            WasInvoked = true;
            throw new InvalidOperationException("consumer-boom");
        }
    }
}
