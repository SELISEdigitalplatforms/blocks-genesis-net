using Blocks.Genesis;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

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
    public void DeserializeBaggage_ShouldReturnEmptyDictionary_ForNull()
    {
        var method = typeof(AzureMessageWorker).GetMethod("DeserializeBaggage", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = (Dictionary<string, string>)method!.Invoke(null, [null])!;

        Assert.Empty(result);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldHandleMissingServiceBusClient()
    {
        var worker = CreateWorkerWithEmptyConnection();

        var exception = await Record.ExceptionAsync(() => InvokePrivateAsync(worker, "ExecuteAsync", CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task ErrorHandler_ShouldCompleteWithoutThrowing()
    {
        var worker = CreateWorkerWithEmptyConnection();
        var errorArgs = (ProcessErrorEventArgs)RuntimeHelpers.GetUninitializedObject(typeof(ProcessErrorEventArgs));
        SetMember(errorArgs, "Exception", new InvalidOperationException("boom"));
        SetMember(errorArgs, "EntityPath", "queue-a");

        var exception = await Record.ExceptionAsync(() => InvokePrivateAsync(worker, "ErrorHandler", errorArgs));

        Assert.Null(exception);
    }

    [Fact]
    public async Task MessageHandler_ShouldCatchOuterError_AndClearContextAndRenewals()
    {
        var originalTestMode = BlocksContext.IsTestMode;
        try
        {
            BlocksContext.IsTestMode = true;
            BlocksContext.ClearContext();
            var worker = CreateWorkerWithEmptyConnection();

            var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
                body: BinaryData.FromString("{\"Type\":\"NoRoute\",\"Body\":\"{}\"}"),
                messageId: "msg-1",
                properties: new Dictionary<string, object>
                {
                    ["TraceId"] = "invalid-trace-id",
                    ["SpanId"] = "invalid-span-id",
                    ["TenantId"] = "tenant-1",
                    ["SecurityContext"] = "{}",
                    ["Baggage"] = "{}"
                });

            var args = (ProcessMessageEventArgs)RuntimeHelpers.GetUninitializedObject(typeof(ProcessMessageEventArgs));
            SetMember(args, "Message", message);

            var started = false;
            worker.MessageProcessingStarted += (_, _) => started = true;

            await InvokePrivateAsync(worker, "MessageHandler", args);

            Assert.True(started);
            AssertClearedContext(BlocksContext.GetContext());

            var renewals = GetField<ConcurrentDictionary<string, CancellationTokenSource>>(worker, "_activeMessageRenewals");
            Assert.DoesNotContain("msg-1", renewals.Keys);
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
        }
    }

    [Fact]
    public async Task MessageHandler_ShouldRunInnerProcessingPath_WhenTraceIdsAreValid()
    {
        var originalTestMode = BlocksContext.IsTestMode;
        try
        {
            BlocksContext.IsTestMode = true;
            BlocksContext.ClearContext();
            var worker = CreateWorkerWithEmptyConnection();

            var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
                body: BinaryData.FromString("{\"Type\":\"NoRoute\",\"Body\":\"{}\"}"),
                messageId: "msg-2",
                properties: new Dictionary<string, object>
                {
                    ["TraceId"] = "0123456789abcdef0123456789abcdef",
                    ["SpanId"] = "0123456789abcdef",
                    ["TenantId"] = "tenant-2",
                    ["SecurityContext"] = "{}",
                    ["Baggage"] = "{}"
                });

            var args = (ProcessMessageEventArgs)RuntimeHelpers.GetUninitializedObject(typeof(ProcessMessageEventArgs));
            SetMember(args, "Message", message);

            var exception = await Record.ExceptionAsync(() => InvokePrivateAsync(worker, "MessageHandler", args));

            Assert.Null(exception);
            AssertClearedContext(BlocksContext.GetContext());
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
        }
    }

    [Fact]
    public async Task StartAutoRenewalTask_ShouldStopImmediately_WhenMaxProcessingTimeExceeded()
    {
        var worker = CreateWorkerWithConfiguration(new MessageConfiguration
        {
            Connection = string.Empty,
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration
            {
                MessageLockRenewalIntervalSeconds = 1,
                MaxMessageProcessingTimeInMinutes = -1
            }
        });

        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString("x"),
            messageId: "renew-1");
        var args = (ProcessMessageEventArgs)RuntimeHelpers.GetUninitializedObject(typeof(ProcessMessageEventArgs));
        SetMember(args, "Message", message);

        using var cts = new CancellationTokenSource();

        var exception = await Record.ExceptionAsync(() => InvokePrivateAsync(worker, "StartAutoRenewalTask", args, cts.Token));

        Assert.Null(exception);
    }

    [Fact]
    public async Task StartAutoRenewalTask_ShouldHandleCancellationDuringDelay()
    {
        var worker = CreateWorkerWithConfiguration(new MessageConfiguration
        {
            Connection = string.Empty,
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration
            {
                MessageLockRenewalIntervalSeconds = 10,
                MaxMessageProcessingTimeInMinutes = 10
            }
        });

        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString("x"),
            messageId: "renew-2");
        var args = (ProcessMessageEventArgs)RuntimeHelpers.GetUninitializedObject(typeof(ProcessMessageEventArgs));
        SetMember(args, "Message", message);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var exception = await Record.ExceptionAsync(() => InvokePrivateAsync(worker, "StartAutoRenewalTask", args, cts.Token));

        Assert.Null(exception);
    }

    [Fact]
    public async Task StopAsync_ShouldCancelAndClearActiveRenewals()
    {
        var worker = CreateWorkerWithEmptyConnection();
        var renewals = GetField<ConcurrentDictionary<string, CancellationTokenSource>>(worker, "_activeMessageRenewals");

        var cts1 = new CancellationTokenSource();
        var cts2 = new CancellationTokenSource();
        renewals.TryAdd("a", cts1);
        renewals.TryAdd("b", cts2);

        await worker.StopAsync(CancellationToken.None);

        Assert.True(cts1.IsCancellationRequested);
        Assert.True(cts2.IsCancellationRequested);
        Assert.Empty(renewals);
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

    private static AzureMessageWorker CreateWorkerWithEmptyConnection()
    {
        return CreateWorkerWithConfiguration(new MessageConfiguration
        {
            Connection = string.Empty,
            AzureServiceBusConfiguration = new AzureServiceBusConfiguration()
        });
    }

    private static AzureMessageWorker CreateWorkerWithConfiguration(MessageConfiguration configuration)
    {
        var logger = new Mock<ILogger<AzureMessageWorker>>();
        var services = new ServiceCollection().BuildServiceProvider();
        var consumerLogger = new Mock<ILogger<Consumer>>();
        var consumer = new Consumer(consumerLogger.Object, services, new RoutingTable(new ServiceCollection()));

        return new AzureMessageWorker(
            logger.Object,
            configuration,
            consumer,
            new ActivitySource("test-azure-worker"));
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

    private static void AssertClearedContext(BlocksContext? context)
    {
        if (context is null)
        {
            return;
        }

        Assert.Equal(string.Empty, context.TenantId);
        Assert.Empty(context.Roles);
        Assert.Equal(string.Empty, context.UserId);
        Assert.False(context.IsAuthenticated);
    }
}
