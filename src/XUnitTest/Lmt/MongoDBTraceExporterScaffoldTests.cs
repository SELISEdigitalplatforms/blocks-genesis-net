using Blocks.Genesis;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenTelemetry;
using SeliseBlocks.LMT.Client;
using System.Collections.Concurrent;
using System.Diagnostics;
using Moq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;

namespace XUnitTest.Lmt;

public class MongoDBTraceExporterScaffoldTests
{
    [Fact]
    public void ConvertToBsonDocument_ShouldMapExpectedFields()
    {
        var trace = new TraceData
        {
            Timestamp = DateTime.UtcNow,
            TraceId = "trace-id",
            SpanId = "span-id",
            ParentSpanId = "parent-span",
            ParentId = "parent-id",
            Kind = "Internal",
            ActivitySourceName = "source",
            OperationName = "op",
            StartTime = DateTime.UtcNow.AddSeconds(-1),
            EndTime = DateTime.UtcNow,
            Duration = 123.4,
            Attributes = new Dictionary<string, object?> { ["count"] = 3 },
            Status = "Ok",
            StatusDescription = "done",
            Baggage = new Dictionary<string, string> { ["TenantId"] = "t-1" },
            ServiceName = "svc",
            TenantId = "t-1"
        };

        var method = typeof(MongoDBTraceExporter).GetMethod("ConvertToBsonDocument", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var doc = (MongoDB.Bson.BsonDocument)method!.Invoke(null, [trace])!;

        Assert.Equal("trace-id", doc["TraceId"].AsString);
        Assert.Equal("svc", doc["ServiceName"].AsString);
        Assert.Equal("t-1", doc["TenantId"].AsString);
        Assert.Equal(3, doc["Attributes"].AsBsonDocument["count"].AsInt32);
    }

    [Fact]
    public void OnEnd_ShouldEnqueueTrace_WithTenantFromBaggage()
    {
        var exporter = CreateExporterForOnEndTests("svc", batchSize: 1000);

        using var listener = CreateActivityListener();
        using var source = new ActivitySource("test-source");
        using var activity = source.StartActivity("op", ActivityKind.Internal)!;
        activity.SetTag("key", "value");

        Baggage.SetBaggage("TenantId", "tenant-42");
        try
        {
            exporter.OnEnd(activity);

            var batch = GetBatch(exporter);
            Assert.Single(batch);

            Assert.True(batch.TryPeek(out var data));
            Assert.NotNull(data);
            Assert.Equal("tenant-42", data!.TenantId);
            Assert.Equal("svc", data.ServiceName);
        }
        finally
        {
            Baggage.SetBaggage("TenantId", null);
        }
    }

    [Fact]
    public void OnEnd_ShouldSkip_WhenTenantBaggageMissing()
    {
        var exporter = CreateExporterForOnEndTests("svc", batchSize: 1000);

        using var listener = CreateActivityListener();
        using var source = new ActivitySource("test-source");
        using var activity = source.StartActivity("op", ActivityKind.Internal)!;

        Baggage.SetBaggage("TenantId", null);

        exporter.OnEnd(activity);

        var batch = GetBatch(exporter);
        Assert.Empty(batch);
    }

    [Fact]
    public void GetBaggageItems_ShouldReturnCurrentBaggage()
    {
        Baggage.SetBaggage("TenantId", "tenant-1");
        Baggage.SetBaggage("UserId", "user-1");
        try
        {
            var method = typeof(MongoDBTraceExporter).GetMethod("GetBaggageItems", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var result = (Dictionary<string, string>)method!.Invoke(null, null)!;

            Assert.Equal("tenant-1", result["TenantId"]);
            Assert.Equal("user-1", result["UserId"]);
        }
        finally
        {
            Baggage.SetBaggage("TenantId", null);
            Baggage.SetBaggage("UserId", null);
        }
    }

    [Fact]
    public async Task FlushBatchAsync_ShouldDrainQueuedItems_WhenNoSinksConfigured()
    {
        var exporter = CreateExporterForOnEndTests("svc", batchSize: 1000);
        var queue = GetBatch(exporter);
        queue.Enqueue(new TraceData { TenantId = "t-1", TraceId = "trace" });

        var method = typeof(MongoDBTraceExporter).GetMethod("FlushBatchAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task)method!.Invoke(exporter, null)!;
        await task;

        Assert.Empty(queue);
    }

    [Fact]
    public async Task SaveToMongoDBAsync_ShouldSwallowInsertExceptions()
    {
        var exporter = CreateExporterForOnEndTests("svc", batchSize: 1000);

        var collection = new Mock<IMongoCollection<BsonDocument>>();
        collection.Setup(c => c.InsertManyAsync(It.IsAny<IEnumerable<BsonDocument>>(), It.IsAny<InsertManyOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("insert failed"));

        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<BsonDocument>("tenant-1", It.IsAny<MongoCollectionSettings>())).Returns(collection.Object);
        SetField(exporter, "_database", database.Object);

        await exporter.SaveToMongoDBAsync(new Dictionary<string, List<TraceData>>
        {
            ["tenant-1"] = [new TraceData { TenantId = "tenant-1", TraceId = "trace-1", SpanId = "span-1" }]
        });

        collection.Verify(c => c.InsertManyAsync(It.IsAny<IEnumerable<BsonDocument>>(), It.IsAny<InsertManyOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Dispose_ShouldNotThrow_WhenExporterIsInitialized()
    {
        var exporter = CreateExporterForOnEndTests("svc", batchSize: 1000);
        SetField(exporter, "_timer", new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite));
        SetField(exporter, "_messageSender", Mock.Of<ILmtMessageSender>());

        var exception = Record.Exception(() => exporter.Dispose());

        Assert.Null(exception);
    }

    [Fact]
    public void Constructor_ShouldNotCreateMessageSender_Eagerly()
    {
        var secret = new Mock<IBlocksSecret>();
        secret.Setup(x => x.TraceConnectionString).Returns(string.Empty);
        secret.Setup(x => x.LmtMessageConnectionString).Returns("Endpoint=sb://secret.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=value");

        var exporter = new MongoDBTraceExporter("svc", blocksSecret: secret.Object);

        var field = typeof(MongoDBTraceExporter).GetField("_messageSender", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        Assert.Null(field!.GetValue(exporter));
    }

    [Fact]
    public void GetOrCreateMessageSender_ShouldPreferLmtConfiguration_OverSecret()
    {
        ILmtMessageSender? sender = null;

        try
        {
            InitializeLmtConfigurationProvider(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Lmt:ConnectionString"] = "amqps://guest:guest@localhost:5672"
                })
                .Build());

            var exporter = CreateExporterForSenderTests("svc", "Endpoint=sb://secret.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=value");
            sender = InvokeGetOrCreateMessageSender(exporter);

            AssertTransportType<LmtRabbitMqSender>(sender);
        }
        finally
        {
            DisposeSenderQuietly(sender);
        }
    }

    [Fact]
    public void GetOrCreateMessageSender_ShouldFallbackToSecret_WhenLmtConfigurationMissing()
    {
        ILmtMessageSender? sender = null;

        try
        {
            InitializeLmtConfigurationProvider(new ConfigurationBuilder().Build());

            var exporter = CreateExporterForSenderTests("svc", "Endpoint=sb://secret.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=value");
            sender = InvokeGetOrCreateMessageSender(exporter);

            AssertTransportType<LmtServiceBusSender>(sender);
        }
        finally
        {
            DisposeSenderQuietly(sender);
        }
    }

    [Fact]
    public void GetOrCreateMessageSender_ShouldIgnoreLegacyServiceBusEnv_WhenLmtConfigurationAndSecretMissing()
    {
        var previousServiceBus = Environment.GetEnvironmentVariable("ServiceBusConnectionString");

        try
        {
            InitializeLmtConfigurationProvider(new ConfigurationBuilder().Build());
            Environment.SetEnvironmentVariable("ServiceBusConnectionString", "Endpoint=sb://legacy.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=value");

            var exporter = CreateExporterForSenderTests("svc", string.Empty);
            var sender = InvokeGetOrCreateMessageSender(exporter);

            Assert.Null(sender);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ServiceBusConnectionString", previousServiceBus);
        }
    }

    private static MongoDBTraceExporter CreateExporterForOnEndTests(string serviceName, int batchSize)
    {
        var exporter = (MongoDBTraceExporter)RuntimeHelpers.GetUninitializedObject(typeof(MongoDBTraceExporter));

        SetField(exporter, "_serviceName", serviceName);
        SetField(exporter, "_batch", new ConcurrentQueue<TraceData>());
        SetField(exporter, "_batchSize", batchSize);
        SetField(exporter, "_maxQueueSize", 10000);
        SetField(exporter, "_semaphore", new SemaphoreSlim(1, 1));

        return exporter;
    }

    private static MongoDBTraceExporter CreateExporterForSenderTests(string serviceName, string lmtMessageConnectionString)
    {
        var exporter = (MongoDBTraceExporter)RuntimeHelpers.GetUninitializedObject(typeof(MongoDBTraceExporter));
        var secret = new Mock<IBlocksSecret>();
        secret.Setup(x => x.LmtMessageConnectionString).Returns(lmtMessageConnectionString);

        SetField(exporter, "_serviceName", serviceName);
        SetField(exporter, "_blocksSecret", secret.Object);
        SetField(exporter, "_semaphore", new SemaphoreSlim(1, 1));

        return exporter;
    }

    private static ConcurrentQueue<TraceData> GetBatch(MongoDBTraceExporter exporter)
    {
        var field = typeof(MongoDBTraceExporter).GetField("_batch", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (ConcurrentQueue<TraceData>)field!.GetValue(exporter)!;
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

    private static void SetField(object instance, string fieldName, object? value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }

    private static ILmtMessageSender? InvokeGetOrCreateMessageSender(MongoDBTraceExporter exporter)
    {
        var method = typeof(MongoDBTraceExporter).GetMethod("GetOrCreateMessageSender", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return (ILmtMessageSender?)method!.Invoke(exporter, null);
    }

    private static void InitializeLmtConfigurationProvider(IConfiguration configuration)
    {
        var providerType = typeof(MongoDBTraceExporter).Assembly.GetType("Blocks.Genesis.LmtConfigurationProvider")!;
        var method = providerType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)!;
        method.Invoke(null, [configuration]);
    }

    private static void DisposeSenderQuietly(ILmtMessageSender? sender)
    {
        if (sender == null)
            return;

        Task.Run(() =>
        {
            try
            {
                sender.Dispose();
            }
            catch
            {
            }
        }).GetAwaiter().GetResult();
    }

    private static void AssertTransportType<TSender>(ILmtMessageSender? sender)
    {
        Assert.NotNull(sender);

        if (sender is TSender)
        {
            return;
        }

        var registrationField = sender!.GetType().GetField("_registration", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(registrationField);

        var registration = registrationField!.GetValue(sender);
        Assert.NotNull(registration);

        var senderProperty = registration!.GetType().GetProperty("Sender", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(senderProperty);
        Assert.IsType<TSender>(senderProperty!.GetValue(registration));
    }
}
