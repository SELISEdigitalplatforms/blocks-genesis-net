using Blocks.Genesis;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using XUnitTest.Delegation;

namespace XUnitTest.Message.Azure;

[Collection("BlocksAuthStaticState")]
public class AzureMessageWorkerCoverageTests
{
    private const string ValidConnection = "Endpoint=sb://unit-test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=01234567890123456789012345678901234567890123456789=";

    [Fact]
    public void Initialization_ShouldCreateClient_WhenConnectionStringHasValidFormat()
    {
        var worker = CreateWorker(new MessageConfiguration
        {
            Connection = ValidConnection,
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration()
        });

        Assert.NotNull(GetField<object?>(worker, "_serviceBusClient"));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStartQueueProcessors_AndFallBackToDefaults_WhenConfigurationDisappearsMidRun()
    {
        var configuration = new MessageConfiguration
        {
            Connection = string.Empty,
            ServiceName = "svc",
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration
            {
                Queues = ["q1", "q2", "q3"],
                Topics = ["t1"]
            }
        };
        var worker = CreateWorker(configuration);

        var clientMock = new Mock<ServiceBusClient>();
        clientMock
            .Setup(x => x.CreateProcessor(It.IsAny<string>(), It.IsAny<ServiceBusProcessorOptions>()))
            .Returns((string queueName, ServiceBusProcessorOptions _) =>
            {
                if (queueName == "q1")
                {
                    configuration.AzureServiceBusConfiguration = null;
                }
                else if (queueName == "q2")
                {
                    SetField(worker, "_messageConfiguration", null);
                }

                return new Mock<ServiceBusProcessor>().Object;
            });
        SetField(worker, "_serviceBusClient", clientMock.Object);

        await InvokePrivateAsync(worker, "ExecuteAsync", CancellationToken.None);

        var processors = GetField<List<ServiceBusProcessor>>(worker, "_processors");
        Assert.Equal(3, processors.Count);
        clientMock.Verify(x => x.CreateProcessor(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ServiceBusProcessorOptions>()), Times.Never);

        // The subscription registered by ExecuteAsync forwards to StartAutoRenewalTask; invoke it
        // with an already cancelled token so it completes synchronously.
        var handler = GetField<EventHandler<AutoRenewalEventArgs>?>(worker, "MessageProcessingStarted");
        Assert.NotNull(handler);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var argsMock = CreateArgsMock(CreateReceivedMessage("evt-1", "x"));
        handler!.Invoke(worker, new AutoRenewalEventArgs { Args = argsMock.Object, Token = cts.Token });

        argsMock.Verify(x => x.RenewMessageLockAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStartTopicProcessors_AndFallBackToDefaults_WhenConfigurationDisappearsMidRun()
    {
        var configuration = new MessageConfiguration
        {
            Connection = string.Empty,
            ServiceName = "svc",
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration
            {
                Queues = [],
                Topics = ["t1", "t2", "t3"]
            }
        };
        var worker = CreateWorker(configuration);

        var subscriptionNames = new List<string?>();
        var clientMock = new Mock<ServiceBusClient>();
        clientMock
            .Setup(x => x.CreateProcessor(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ServiceBusProcessorOptions>()))
            .Returns((string topicName, string subscriptionName, ServiceBusProcessorOptions _) =>
            {
                subscriptionNames.Add(subscriptionName);
                if (topicName == "t1")
                {
                    configuration.AzureServiceBusConfiguration = null;
                }
                else if (topicName == "t2")
                {
                    SetField(worker, "_messageConfiguration", null);
                }

                return new Mock<ServiceBusProcessor>().Object;
            });
        SetField(worker, "_serviceBusClient", clientMock.Object);

        await InvokePrivateAsync(worker, "ExecuteAsync", CancellationToken.None);

        Assert.Equal(3, GetField<List<ServiceBusProcessor>>(worker, "_processors").Count);
        Assert.Equal("t1_sub_svc", subscriptionNames[0]);
        Assert.Null(subscriptionNames[2]);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStartNoProcessors_WhenAzureServiceBusConfigurationIsNull()
    {
        var worker = CreateWorker(new MessageConfiguration
        {
            Connection = string.Empty,
            AzureServiceBusConfiguration = null
        });

        var clientMock = new Mock<ServiceBusClient>();
        SetField(worker, "_serviceBusClient", clientMock.Object);

        await InvokePrivateAsync(worker, "ExecuteAsync", CancellationToken.None);

        Assert.Empty(GetField<List<ServiceBusProcessor>>(worker, "_processors"));
        clientMock.Verify(x => x.CreateProcessor(It.IsAny<string>(), It.IsAny<ServiceBusProcessorOptions>()), Times.Never);
        clientMock.Verify(x => x.CreateProcessor(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ServiceBusProcessorOptions>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStartNoProcessors_WhenConfigurationIsNull()
    {
        var worker = CreateWorker(EmptyConfiguration());
        var clientMock = new Mock<ServiceBusClient>();
        SetField(worker, "_serviceBusClient", clientMock.Object);
        SetField(worker, "_messageConfiguration", null);

        await InvokePrivateAsync(worker, "ExecuteAsync", CancellationToken.None);

        Assert.Empty(GetField<List<ServiceBusProcessor>>(worker, "_processors"));
        clientMock.Verify(x => x.CreateProcessor(It.IsAny<string>(), It.IsAny<ServiceBusProcessorOptions>()), Times.Never);
        clientMock.Verify(x => x.CreateProcessor(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ServiceBusProcessorOptions>()), Times.Never);
    }

    [Fact]
    public async Task StopAsync_ShouldStopAndDisposeProcessors_AndHandleStopFailures()
    {
        var worker = CreateWorker(EmptyConfiguration());

        var failingProcessor = new Mock<ServiceBusProcessor>();
        failingProcessor
            .Setup(x => x.StopProcessingAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("stop-failed"));
        var succeedingProcessor = new Mock<ServiceBusProcessor>();

        var processors = GetField<List<ServiceBusProcessor>>(worker, "_processors");
        processors.Add(failingProcessor.Object);
        processors.Add(succeedingProcessor.Object);

        var exception = await Record.ExceptionAsync(() => worker.StopAsync(CancellationToken.None));

        Assert.Null(exception);
        failingProcessor.Verify(x => x.StopProcessingAsync(It.IsAny<CancellationToken>()), Times.Once);
        succeedingProcessor.Verify(x => x.StopProcessingAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MessageHandler_ShouldCompleteMessage_WhenProcessingSucceeds()
    {
        var originalTestMode = BlocksContext.IsTestMode;
        var sourceName = "worker-cov-" + Guid.NewGuid().ToString("N");
        using var listener = CreateListener(sourceName);
        try
        {
            BlocksContext.IsTestMode = true;
            BlocksContext.ClearContext();
            var worker = CreateWorker(EmptyConfiguration(), sourceName);

            var message = CreateReceivedMessage("ok-1", "{\"Type\":\"NoRoute\",\"Body\":\"{}\"}", new Dictionary<string, object>
            {
                ["TraceId"] = "0123456789abcdef0123456789abcdef",
                ["SpanId"] = "0123456789abcdef",
                ["TenantId"] = "tenant-1",
                ["SecurityContext"] = "{}",
                ["Baggage"] = "{\"bag-key\":\"bag-value\"}"
            });
            var argsMock = CreateArgsMock(message);

            await InvokePrivateAsync(worker, "MessageHandler", argsMock.Object);

            argsMock.Verify(x => x.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
            argsMock.Verify(x => x.DeadLetterMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.Empty(GetField<ConcurrentDictionary<string, CancellationTokenSource>>(worker, "_activeMessageRenewals"));
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
        }
    }

    [Fact]
    public async Task MessageHandler_ShouldDeadLetterMessage_WhenProcessingFails()
    {
        var originalTestMode = BlocksContext.IsTestMode;
        var sourceName = "worker-cov-" + Guid.NewGuid().ToString("N");
        using var listener = CreateListener(sourceName);
        try
        {
            BlocksContext.IsTestMode = true;
            BlocksContext.ClearContext();
            var worker = CreateWorker(EmptyConfiguration(), sourceName);

            var message = CreateReceivedMessage("fail-1", "not-json", new Dictionary<string, object>
            {
                ["TraceId"] = "0123456789abcdef0123456789abcdef",
                ["SpanId"] = "0123456789abcdef",
                ["TenantId"] = "tenant-1",
                ["SecurityContext"] = "{}",
                ["Baggage"] = "{}"
            });
            var argsMock = CreateArgsMock(message);

            await InvokePrivateAsync(worker, "MessageHandler", argsMock.Object);

            argsMock.Verify(x => x.CompleteMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()), Times.Never);
            argsMock.Verify(x => x.DeadLetterMessageAsync(message, "processing_failed", "JsonException", It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
        }
    }

    [Fact]
    public async Task MessageHandler_ShouldLogDeadLetterFailure_WhenDeadLetterThrows()
    {
        var originalTestMode = BlocksContext.IsTestMode;
        try
        {
            BlocksContext.IsTestMode = true;
            BlocksContext.ClearContext();
            var worker = CreateWorker(EmptyConfiguration());

            var message = CreateReceivedMessage("fail-2", "not-json", new Dictionary<string, object>
            {
                ["TraceId"] = "0123456789abcdef0123456789abcdef",
                ["SpanId"] = "0123456789abcdef",
                ["SecurityContext"] = "{}",
                ["Baggage"] = "{}"
            });
            var argsMock = CreateArgsMock(message);
            argsMock
                .Setup(x => x.DeadLetterMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("dead-letter-failed"));

            var exception = await Record.ExceptionAsync(() => InvokePrivateAsync(worker, "MessageHandler", argsMock.Object));

            Assert.Null(exception);
            argsMock.Verify(x => x.DeadLetterMessageAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
        }
    }

    [Fact]
    public async Task MessageHandler_ShouldFallBackToDefaults_WhenApplicationPropertiesAreMissing()
    {
        var originalTestMode = BlocksContext.IsTestMode;
        try
        {
            BlocksContext.IsTestMode = true;
            BlocksContext.ClearContext();
            var worker = CreateWorker(EmptyConfiguration());

            var message = CreateReceivedMessage("missing-props", "{\"Type\":\"NoRoute\",\"Body\":\"{}\"}");
            var argsMock = CreateArgsMock(message);

            var exception = await Record.ExceptionAsync(() => InvokePrivateAsync(worker, "MessageHandler", argsMock.Object));

            Assert.Null(exception);
            Assert.Empty(GetField<ConcurrentDictionary<string, CancellationTokenSource>>(worker, "_activeMessageRenewals"));
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
        }
    }

    [Fact]
    public async Task MessageHandler_ShouldCreateRandomSpanId_WhenSpanIdValueHasNullText()
    {
        var originalTestMode = BlocksContext.IsTestMode;
        try
        {
            BlocksContext.IsTestMode = true;
            BlocksContext.ClearContext();
            var worker = CreateWorker(EmptyConfiguration());

            var message = CreateReceivedMessage("null-span", "{\"Type\":\"NoRoute\",\"Body\":\"{}\"}", new Dictionary<string, object>
            {
                ["TraceId"] = "0123456789abcdef0123456789abcdef",
                ["SpanId"] = new NullToStringValue()
            });
            var argsMock = CreateArgsMock(message);

            await InvokePrivateAsync(worker, "MessageHandler", argsMock.Object);

            argsMock.Verify(x => x.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"Type\":null,\"Body\":null}")]
    public async Task MessageHandler_ShouldCompleteMessage_WhenEnvelopeDeserializesToNullOrEmpty(string body)
    {
        var originalTestMode = BlocksContext.IsTestMode;
        try
        {
            BlocksContext.IsTestMode = true;
            BlocksContext.ClearContext();
            var worker = CreateWorker(EmptyConfiguration());

            var message = CreateReceivedMessage("null-envelope", body, new Dictionary<string, object>
            {
                ["TraceId"] = "0123456789abcdef0123456789abcdef",
                ["SpanId"] = "0123456789abcdef",
                ["SecurityContext"] = "{}",
                ["Baggage"] = "{}"
            });
            var argsMock = CreateArgsMock(message);

            await InvokePrivateAsync(worker, "MessageHandler", argsMock.Object);

            argsMock.Verify(x => x.CompleteMessageAsync(message, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
        }
    }

    [Fact]
    public async Task StartAutoRenewalTask_ShouldRenewLock_AndStop_WhenServiceBusExceptionOccurs()
    {
        var worker = CreateWorker(RenewalConfiguration());
        var argsMock = CreateArgsMock(CreateReceivedMessage("renew-ok", "x"));
        argsMock
            .SetupSequence(x => x.RenewMessageLockAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .ThrowsAsync(new ServiceBusException("lock-lost", ServiceBusFailureReason.MessageLockLost));

        using var cts = new CancellationTokenSource();
        var exception = await Record.ExceptionAsync(() => InvokePrivateAsync(worker, "StartAutoRenewalTask", argsMock.Object, cts.Token));

        Assert.Null(exception);
        argsMock.Verify(x => x.RenewMessageLockAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task StartAutoRenewalTask_ShouldUseDefaults_WhenConfigurationDisappearsMidRun()
    {
        var configuration = RenewalConfiguration();
        var worker = CreateWorker(configuration);
        var argsMock = CreateArgsMock(CreateReceivedMessage("renew-mutate", "x"));

        var renewals = 0;
        argsMock
            .Setup(x => x.RenewMessageLockAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                renewals++;
                if (renewals == 1)
                {
                    configuration.AzureServiceBusConfiguration = null;
                    return Task.CompletedTask;
                }

                if (renewals == 2)
                {
                    SetField(worker, "_messageConfiguration", null);
                    return Task.CompletedTask;
                }

                return Task.FromException(new ServiceBusException("stop", ServiceBusFailureReason.MessageLockLost));
            });

        using var cts = new CancellationTokenSource();
        var exception = await Record.ExceptionAsync(() => InvokePrivateAsync(worker, "StartAutoRenewalTask", argsMock.Object, cts.Token));

        Assert.Null(exception);
        Assert.Equal(3, renewals);
    }

    [Fact]
    public async Task StartAutoRenewalTask_ShouldCatchUnexpectedException()
    {
        var worker = CreateWorker(RenewalConfiguration());
        var argsMock = CreateArgsMock(CreateReceivedMessage("renew-error", "x"));
        argsMock
            .Setup(x => x.RenewMessageLockAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("unexpected"));

        using var cts = new CancellationTokenSource();
        var exception = await Record.ExceptionAsync(() => InvokePrivateAsync(worker, "StartAutoRenewalTask", argsMock.Object, cts.Token));

        Assert.Null(exception);
        argsMock.Verify(x => x.RenewMessageLockAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartAutoRenewalTask_ShouldCompleteImmediately_WhenTokenIsCancelledAndConfigurationIsMissing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var workerWithoutBusConfiguration = CreateWorker(new MessageConfiguration
        {
            Connection = string.Empty,
            AzureServiceBusConfiguration = null
        });
        var workerWithoutConfiguration = CreateWorker(EmptyConfiguration());
        SetField(workerWithoutConfiguration, "_messageConfiguration", null);

        var argsMock = CreateArgsMock(CreateReceivedMessage("renew-cancelled", "x"));

        await InvokePrivateAsync(workerWithoutBusConfiguration, "StartAutoRenewalTask", argsMock.Object, cts.Token);
        await InvokePrivateAsync(workerWithoutConfiguration, "StartAutoRenewalTask", argsMock.Object, cts.Token);

        argsMock.Verify(x => x.RenewMessageLockAsync(It.IsAny<ServiceBusReceivedMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void DeserializeBaggage_ShouldReturnEmptyDictionary_WhenJsonIsNullLiteral()
    {
        var method = typeof(AzureMessageWorker).GetMethod("DeserializeBaggage", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = (Dictionary<string, string>)method!.Invoke(null, ["null"])!;

        Assert.Empty(result);
    }

    private static MessageConfiguration EmptyConfiguration()
    {
        return new MessageConfiguration
        {
            Connection = string.Empty,
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration()
        };
    }

    private static MessageConfiguration RenewalConfiguration()
    {
        return new MessageConfiguration
        {
            Connection = string.Empty,
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration
            {
                MessageLockRenewalIntervalSeconds = 0,
                MaxMessageProcessingTimeInMinutes = 60
            }
        };
    }

    private static AzureMessageWorker CreateWorker(MessageConfiguration configuration, string? activitySourceName = null)
    {
        var logger = new Mock<ILogger<AzureMessageWorker>>();
        var services = new ServiceCollection().BuildServiceProvider();
        var consumerLogger = new Mock<ILogger<Consumer>>();
        var consumer = new Consumer(consumerLogger.Object, services, new RoutingTable(new ServiceCollection()));

        return new AzureMessageWorker(
            logger.Object,
            configuration,
            consumer,
            new ActivitySource(activitySourceName ?? "test-azure-worker-coverage"),
            DelegationTestDoubles.NoOpStore(),
            DelegationTestDoubles.NoOpProvider());
    }

    private static ServiceBusReceivedMessage CreateReceivedMessage(string messageId, string body, IDictionary<string, object>? properties = null)
    {
        return ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(body),
            messageId: messageId,
            properties: properties);
    }

    private static Mock<ProcessMessageEventArgs> CreateArgsMock(ServiceBusReceivedMessage message)
    {
        var receiver = new Mock<ServiceBusReceiver>();
        return new Mock<ProcessMessageEventArgs>(message, receiver.Object, CancellationToken.None);
    }

    private static ActivityListener CreateListener(string sourceName)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static async Task InvokePrivateAsync(object instance, string methodName, params object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = (Task)method!.Invoke(instance, args)!;
        await task;
    }

    private static T GetField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (T)field!.GetValue(instance)!;
    }

    private static void SetField(object instance, string fieldName, object? value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }

    private sealed class NullToStringValue
    {
        public override string? ToString() => null;
    }
}
