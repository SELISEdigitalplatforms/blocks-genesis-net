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
using XUnitTest.Delegation;

namespace XUnitTest.Message.RabbitMq;

[Collection("BlocksAuthStaticState")]
public class RabbitMessageWorkerCoverageTests
{
    [Fact]
    public async Task HandleMessageAsync_ShouldProcessSequentially_WhenRoutingKeyHasNoSubscription()
    {
        CoverageConsumerProbe.Reset();

        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumerWithProbe(), CreateConfiguration("orders.queue"));
        SetPrivateChannel(worker, channel.Object);

        var ea = CreateEventArgs("unknown.queue", CreateEnvelope("ok"), deliveryTag: 11);

        await InvokePrivateAsync(worker, "HandleMessageAsync", [new object(), ea]);

        Assert.Equal("ok", CoverageConsumerProbe.LastValue);
        channel.Verify(x => x.BasicAckAsync(11, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleMessageAsync_ShouldProcessInParallel_AndAck_WhenSubscriptionIsParallel()
    {
        CoverageConsumerProbe.Reset();

        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);

        var acked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel
            .Setup(x => x.BasicAckAsync(It.IsAny<ulong>(), false, It.IsAny<CancellationToken>()))
            .Callback(() => acked.TrySetResult(true))
            .Returns(ValueTask.CompletedTask);

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumerWithProbe(), CreateParallelConfiguration("parallel.queue"));
        SetPrivateChannel(worker, channel.Object);

        var ea = CreateEventArgs("parallel.queue", CreateEnvelope("parallel-ok"), deliveryTag: 21);

        await InvokePrivateAsync(worker, "HandleMessageAsync", [new object(), ea]);

        Assert.True(await acked.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("parallel-ok", CoverageConsumerProbe.LastValue);
        channel.Verify(x => x.BasicAckAsync(21, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleMessageAsync_ShouldNackViaParallelFaultHandler_WhenParallelProcessingFaults()
    {
        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        SetupLoggerThrowFor(logger, "Unexpected error in message processing");

        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);

        var nacked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel
            .Setup(x => x.BasicNackAsync(It.IsAny<ulong>(), false, false, It.IsAny<CancellationToken>()))
            .Callback(() => nacked.TrySetResult(true))
            .Returns(ValueTask.CompletedTask);

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumer(), CreateParallelConfiguration("parallel.queue"));
        SetPrivateChannel(worker, channel.Object);

        var ea = CreateEventArgs("parallel.queue", CreateEnvelope("x"), deliveryTag: 31, includeSecurityContext: false);

        await InvokePrivateAsync(worker, "HandleMessageAsync", [new object(), ea]);

        Assert.True(await nacked.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        channel.Verify(x => x.BasicNackAsync(31, false, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleMessageAsync_ShouldNackViaCriticalHandler_WhenSequentialProcessingFaults()
    {
        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        SetupLoggerThrowFor(logger, "Unexpected error in message processing");

        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumer(), CreateConfiguration("orders.queue"));
        SetPrivateChannel(worker, channel.Object);

        var ea = CreateEventArgs("orders.queue", CreateEnvelope("x"), deliveryTag: 41, includeSecurityContext: false);

        var exception = await Record.ExceptionAsync(() => InvokePrivateAsync(worker, "HandleMessageAsync", [new object(), ea]));

        Assert.Null(exception);
        channel.Verify(x => x.BasicNackAsync(41, false, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleMessageAsync_ShouldNack_WhenSecurityContextHeaderIsMissing()
    {
        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumer(), CreateConfiguration("orders.queue"));
        SetPrivateChannel(worker, channel.Object);

        var ea = CreateEventArgs("orders.queue", CreateEnvelope("x"), deliveryTag: 51, includeSecurityContext: false);

        await InvokePrivateAsync(worker, "HandleMessageAsync", [new object(), ea]);

        channel.Verify(x => x.BasicNackAsync(51, false, false, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(x => x.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleMessageAsync_ShouldSkipNack_WhenChannelClosedAfterProcessingError()
    {
        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(false);

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumer(), CreateConfiguration("orders.queue"));
        SetPrivateChannel(worker, channel.Object);

        var ea = CreateEventArgs("orders.queue", CreateEnvelope("x"), deliveryTag: 61, includeSecurityContext: false);

        await InvokePrivateAsync(worker, "HandleMessageAsync", [new object(), ea]);

        channel.Verify(x => x.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleMessageAsync_ShouldSwallow_WhenNackItselfThrows()
    {
        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);
        channel
            .Setup(x => x.BasicNackAsync(It.IsAny<ulong>(), false, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("nack failed"));

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumer(), CreateConfiguration("orders.queue"));
        SetPrivateChannel(worker, channel.Object);

        var ea = CreateEventArgs("orders.queue", CreateEnvelope("x"), deliveryTag: 71, includeSecurityContext: false);

        var exception = await Record.ExceptionAsync(() => InvokePrivateAsync(worker, "HandleMessageAsync", [new object(), ea]));

        Assert.Null(exception);
    }

    [Fact]
    public async Task HandleMessageAsync_ShouldAck_WhenEnvelopeDeserializesToNull()
    {
        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumer(), CreateConfiguration("orders.queue"));
        SetPrivateChannel(worker, channel.Object);

        var ea = CreateEventArgs("orders.queue", "null", deliveryTag: 81);

        await InvokePrivateAsync(worker, "HandleMessageAsync", [new object(), ea]);

        channel.Verify(x => x.BasicAckAsync(81, false, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(x => x.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleMessageAsync_ShouldNack_WhenDispatchFailsAfterConsumerRan()
    {
        CoverageConsumerProbe.Reset();

        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        SetupLoggerThrowFor(logger, "Message processed successfully");

        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumerWithProbe(), CreateConfiguration("orders.queue"));
        SetPrivateChannel(worker, channel.Object);

        var ea = CreateEventArgs("orders.queue", CreateEnvelope("dispatch-fault"), deliveryTag: 91);

        await InvokePrivateAsync(worker, "HandleMessageAsync", [new object(), ea]);

        Assert.Equal("dispatch-fault", CoverageConsumerProbe.LastValue);
        channel.Verify(x => x.BasicNackAsync(91, false, false, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(x => x.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleMessageAsync_ShouldSkipNack_WhenChannelClosedAfterDispatchFailure()
    {
        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        SetupLoggerThrowFor(logger, "Message processed successfully");

        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(false);

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumerWithProbe(), CreateConfiguration("orders.queue"));
        SetPrivateChannel(worker, channel.Object);

        var ea = CreateEventArgs("orders.queue", CreateEnvelope("x"), deliveryTag: 101);

        await InvokePrivateAsync(worker, "HandleMessageAsync", [new object(), ea]);

        channel.Verify(x => x.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        channel.Verify(x => x.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleMessageAsync_ShouldSkipAck_WhenChannelClosedAfterSuccess()
    {
        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(false);

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumerWithProbe(), CreateConfiguration("orders.queue"));
        SetPrivateChannel(worker, channel.Object);

        var ea = CreateEventArgs("orders.queue", CreateEnvelope("x"), deliveryTag: 111);

        await InvokePrivateAsync(worker, "HandleMessageAsync", [new object(), ea]);

        channel.Verify(x => x.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleMessageAsync_ShouldTagActivity_WhenListenerIsActive()
    {
        using var listener = CreateActivityListener();

        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumerWithProbe(), CreateConfiguration("orders.queue"));
        SetPrivateChannel(worker, channel.Object);

        var ea = CreateEventArgs("orders.queue", CreateEnvelope("with-activity"), deliveryTag: 121);

        await InvokePrivateAsync(worker, "HandleMessageAsync", [new object(), ea]);

        channel.Verify(x => x.BasicAckAsync(121, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleMessageAsync_ShouldTagActivityError_WhenPayloadIsInvalidJsonAndListenerIsActive()
    {
        using var listener = CreateActivityListener();

        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumer(), CreateConfiguration("orders.queue"));
        SetPrivateChannel(worker, channel.Object);

        var ea = CreateEventArgs("orders.queue", "not-json", deliveryTag: 131);

        await InvokePrivateAsync(worker, "HandleMessageAsync", [new object(), ea]);

        channel.Verify(x => x.BasicAckAsync(131, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleMessageAsync_ShouldSkipNack_WhenChannelIsNullAfterProcessingError()
    {
        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var rabbitService = new Mock<IRabbitMqService>();

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumer(), CreateConfiguration("orders.queue"));

        var ea = CreateEventArgs("orders.queue", CreateEnvelope("x"), deliveryTag: 141, includeSecurityContext: false);

        var exception = await Record.ExceptionAsync(() => InvokePrivateAsync(worker, "HandleMessageAsync", [new object(), ea]));

        Assert.Null(exception);
    }

    [Fact]
    public async Task HandleMessageAsync_ShouldSkipAck_WhenChannelIsNullAfterSuccess()
    {
        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var rabbitService = new Mock<IRabbitMqService>();

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumerWithProbe(), CreateConfiguration("orders.queue"));

        var ea = CreateEventArgs("orders.queue", CreateEnvelope("x"), deliveryTag: 151);

        var exception = await Record.ExceptionAsync(() => InvokePrivateAsync(worker, "HandleMessageAsync", [new object(), ea]));

        Assert.Null(exception);
    }

    [Fact]
    public async Task HandleMessageAsync_ShouldSkipNack_WhenChannelIsNullAfterDispatchFailure()
    {
        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        SetupLoggerThrowFor(logger, "Message processed successfully");

        var rabbitService = new Mock<IRabbitMqService>();

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumerWithProbe(), CreateConfiguration("orders.queue"));

        var ea = CreateEventArgs("orders.queue", CreateEnvelope("x"), deliveryTag: 161);

        var exception = await Record.ExceptionAsync(() => InvokePrivateAsync(worker, "HandleMessageAsync", [new object(), ea]));

        Assert.Null(exception);
    }

    [Fact]
    public async Task HandleMessageAsync_ShouldTagActivityError_WhenDispatchFailsAndListenerIsActive()
    {
        using var listener = CreateActivityListener();

        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        SetupLoggerThrowFor(logger, "Message processed successfully");

        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumerWithProbe(), CreateConfiguration("orders.queue"));
        SetPrivateChannel(worker, channel.Object);

        var ea = CreateEventArgs("orders.queue", CreateEnvelope("x"), deliveryTag: 171);

        await InvokePrivateAsync(worker, "HandleMessageAsync", [new object(), ea]);

        channel.Verify(x => x.BasicNackAsync(171, false, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAndAckAsync_ShouldAck_WhenPayloadIsInvalidJsonAndActivityIsNull()
    {
        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumer(), CreateConfiguration("orders.queue"));
        SetPrivateChannel(worker, channel.Object);

        var ea = CreateEventArgs("orders.queue", "not-json", deliveryTag: 181);

        await InvokePrivateAsync(worker, "DispatchAndAckAsync", [Encoding.UTF8.GetBytes("not-json"), null, ea, null]);

        channel.Verify(x => x.BasicAckAsync(181, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void ApplyBaggage_ShouldUseEmptyJson_WhenBaggageIsNull()
    {
        var worker = CreateDefaultWorker();

        var exception = Record.Exception(() => InvokePrivate(worker, "ApplyBaggage", [null, "tenant-no-baggage"]));

        Assert.Null(exception);
        Assert.Equal("tenant-no-baggage", OpenTelemetry.Baggage.GetBaggage("TenantId"));
    }

    [Fact]
    public void ApplyBaggage_ShouldSetEntries_WhenBaggageIsValidJson()
    {
        var worker = CreateDefaultWorker();

        var exception = Record.Exception(() => InvokePrivate(worker, "ApplyBaggage", ["{\"cov-key\":\"cov-value\"}", "tenant-baggage"]));

        Assert.Null(exception);
        Assert.Equal("cov-value", OpenTelemetry.Baggage.GetBaggage("cov-key"));
        Assert.Equal("tenant-baggage", OpenTelemetry.Baggage.GetBaggage("TenantId"));
    }

    [Fact]
    public void ApplyBaggage_ShouldFallBackToEmpty_WhenBaggageDeserializesToNull()
    {
        var worker = CreateDefaultWorker();

        var exception = Record.Exception(() => InvokePrivate(worker, "ApplyBaggage", ["null", "tenant-null"]));

        Assert.Null(exception);
        Assert.Equal("tenant-null", OpenTelemetry.Baggage.GetBaggage("TenantId"));
    }

    [Fact]
    public void ApplyBaggage_ShouldLogWarning_WhenBaggageIsInvalidJson()
    {
        var worker = CreateDefaultWorker();

        var exception = Record.Exception(() => InvokePrivate(worker, "ApplyBaggage", ["not-json", "tenant-invalid"]));

        Assert.Null(exception);
        Assert.Equal("tenant-invalid", OpenTelemetry.Baggage.GetBaggage("TenantId"));
    }

    [Fact]
    public void BuildParentActivityContext_ShouldCreateRandomIds_WhenTraceAndSpanAreNull()
    {
        var worker = CreateDefaultWorker();

        var context = (ActivityContext)InvokePrivate(worker, "BuildParentActivityContext", [null, null])!;

        Assert.NotEqual(default, context.TraceId);
        Assert.NotEqual(default, context.SpanId);
        Assert.True(context.IsRemote);
    }

    [Fact]
    public void BuildParentActivityContext_ShouldUseProvidedTrace_WhenSpanIsNull()
    {
        var worker = CreateDefaultWorker();

        var context = (ActivityContext)InvokePrivate(worker, "BuildParentActivityContext", ["0123456789abcdef0123456789abcdef", null])!;

        Assert.Equal("0123456789abcdef0123456789abcdef", context.TraceId.ToString());
        Assert.True(context.IsRemote);
    }

    [Fact]
    public void BuildParentActivityContext_ShouldCreateFallbackContext_WhenTraceIdIsInvalid()
    {
        var worker = CreateDefaultWorker();

        var context = (ActivityContext)InvokePrivate(worker, "BuildParentActivityContext", ["zz-not-a-trace-id", "0123456789abcdef"])!;

        Assert.NotEqual(default, context.TraceId);
        Assert.False(context.IsRemote);
    }

    [Fact]
    public void SetSecurityContextFromHeader_ShouldClearContext_WhenHeaderIsInvalidJson()
    {
        var method = typeof(RabbitMessageWorker).GetMethod("SetSecurityContextFromHeader", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        BlocksContext.SetContext(BlocksContext.Create("tenant-before", [], "", false, "", "", DateTime.MinValue, "", [], "", "", "", "", "tenant-before"));
        try
        {
            method!.Invoke(null, ["not-json"]);

            Assert.Null(BlocksContext.GetContext());
        }
        finally
        {
            BlocksContext.ClearContext();
        }
    }

    [Fact]
    public async Task StopAsync_ShouldComplete_WhenChannelWasNeverInitialized()
    {
        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var worker = CreateDefaultWorker(logger.Object);

        var exception = await Record.ExceptionAsync(() => worker.StopAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task StopAsync_ShouldStillDispose_WhenCloseThrows()
    {
        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);
        channel
            .Setup(x => x.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("close failed"));

        var worker = CreateDefaultWorker(logger.Object);
        SetPrivateChannel(worker, channel.Object);

        var exception = await Record.ExceptionAsync(() => worker.StopAsync(CancellationToken.None));

        Assert.Null(exception);
        channel.Verify(x => x.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task StopAsync_ShouldSwallow_WhenDisposeThrows()
    {
        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Returns(true);
        channel.Setup(x => x.DisposeAsync()).ThrowsAsync(new InvalidOperationException("dispose failed"));

        var worker = CreateDefaultWorker(logger.Object);
        SetPrivateChannel(worker, channel.Object);

        var exception = await Record.ExceptionAsync(() => worker.StopAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task StopAsync_ShouldSwallow_WhenChannelStateCheckThrows()
    {
        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(x => x.IsOpen).Throws(new InvalidOperationException("state check failed"));

        var worker = CreateDefaultWorker(logger.Object);
        SetPrivateChannel(worker, channel.Object);

        var exception = await Record.ExceptionAsync(() => worker.StopAsync(CancellationToken.None));

        Assert.Null(exception);
        channel.Verify(x => x.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartConsumingAsync_ShouldNotConsume_WhenMessageConfigurationIsNull()
    {
        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumer(), null!);
        SetPrivateChannel(worker, channel.Object);

        await InvokePrivateAsync(worker, "StartConsumingAsync", [new AsyncEventingBasicConsumer(channel.Object)]);

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

    [Fact]
    public async Task StartConsumingAsync_ShouldNotConsume_WhenRabbitMqConfigurationIsNull()
    {
        var logger = new Mock<ILogger<RabbitMessageWorker>>();
        var rabbitService = new Mock<IRabbitMqService>();
        var channel = new Mock<IChannel>();

        var worker = CreateWorker(logger.Object, rabbitService.Object, CreateConsumer(), new MessageConfiguration());
        SetPrivateChannel(worker, channel.Object);

        await InvokePrivateAsync(worker, "StartConsumingAsync", [new AsyncEventingBasicConsumer(channel.Object)]);

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

    private static void SetupLoggerThrowFor(Mock<ILogger<RabbitMessageWorker>> logger, string messageFragment)
    {
        logger
            .Setup(x => x.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(messageFragment)),
                It.IsAny<Exception?>(),
                (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()))
            .Throws(new InvalidOperationException($"logger failure for '{messageFragment}'"));
    }

    private static RabbitMessageWorker CreateDefaultWorker(ILogger<RabbitMessageWorker>? logger = null)
    {
        return CreateWorker(
            logger ?? new Mock<ILogger<RabbitMessageWorker>>().Object,
            new Mock<IRabbitMqService>().Object,
            CreateConsumer(),
            CreateConfiguration("orders.queue"));
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
            new ActivitySource("test-worker-coverage"),
            DelegationTestDoubles.NoOpStore(),
            DelegationTestDoubles.NoOpProvider());
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

    private static MessageConfiguration CreateParallelConfiguration(string queueName)
    {
        return new MessageConfiguration
        {
            RabbitMqConfiguration = new RabbitMqConfiguration
            {
                ConsumerSubscriptions =
                [
                    new ConsumerSubscription(queueName, string.Empty, 3, parallelProcessing: true)
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
        serviceCollection.AddSingleton<IConsumer<CoveragePayload>, CoverageConsumerProbe>();

        var routing = new RoutingTable(serviceCollection);
        var serviceProvider = serviceCollection.BuildServiceProvider();
        return new Consumer(consumerLogger.Object, serviceProvider, routing);
    }

    private static string CreateEnvelope(string value)
    {
        var payload = JsonSerializer.Serialize(new CoveragePayload { Value = value });
        return JsonSerializer.Serialize(new global::Blocks.Genesis.Message { Type = nameof(CoveragePayload), Body = payload });
    }

    private static void SetPrivateChannel(RabbitMessageWorker worker, IChannel channel)
    {
        var field = typeof(RabbitMessageWorker).GetField("_channel", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(worker, channel);
    }

    private static async Task InvokePrivateAsync(object instance, string methodName, object?[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task)method!.Invoke(instance, args)!;
        await task;
    }

    private static object? InvokePrivate(object instance, string methodName, object?[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        try
        {
            return method!.Invoke(instance, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }

    private static BasicDeliverEventArgs CreateEventArgs(string routingKey, string body, ulong deliveryTag, bool includeSecurityContext = true)
    {
        var ea = (BasicDeliverEventArgs)RuntimeHelpers.GetUninitializedObject(typeof(BasicDeliverEventArgs));

        var headers = new Dictionary<string, object?>
        {
            ["TenantId"] = Encoding.UTF8.GetBytes("tenant-cov"),
            ["TraceId"] = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef"),
            ["SpanId"] = Encoding.UTF8.GetBytes("0123456789abcdef"),
            ["Baggage"] = Encoding.UTF8.GetBytes("{}")
        };

        if (includeSecurityContext)
        {
            headers["SecurityContext"] = Encoding.UTF8.GetBytes("{}");
        }

        var properties = new BasicProperties
        {
            Headers = headers
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

    private static ActivityListener CreateActivityListener()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class CoveragePayload
    {
        public string? Value { get; set; }
    }

    private sealed class CoverageConsumerProbe : IConsumer<CoveragePayload>
    {
        public static string? LastValue { get; private set; }

        public static void Reset() => LastValue = null;

        public Task Consume(CoveragePayload context)
        {
            LastValue = context.Value;
            return Task.CompletedTask;
        }
    }
}
