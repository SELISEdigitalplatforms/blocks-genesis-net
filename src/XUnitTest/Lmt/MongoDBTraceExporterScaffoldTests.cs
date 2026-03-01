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
    public void OnEnd_ShouldFallbackToMiscellaneous_WhenTenantBaggageMissing()
    {
        var exporter = CreateExporterForOnEndTests("svc", batchSize: 1000);

        using var listener = CreateActivityListener();
        using var source = new ActivitySource("test-source");
        using var activity = source.StartActivity("op", ActivityKind.Internal)!;

        Baggage.SetBaggage("TenantId", null);

        exporter.OnEnd(activity);

        var batch = GetBatch(exporter);
        Assert.True(batch.TryPeek(out var data));
        Assert.Equal(BlocksConstants.Miscellaneous, data!.TenantId);
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

    private static MongoDBTraceExporter CreateExporterForOnEndTests(string serviceName, int batchSize)
    {
        var exporter = (MongoDBTraceExporter)RuntimeHelpers.GetUninitializedObject(typeof(MongoDBTraceExporter));

        SetField(exporter, "_serviceName", serviceName);
        SetField(exporter, "_batch", new ConcurrentQueue<TraceData>());
        SetField(exporter, "_batchSize", batchSize);
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
}
