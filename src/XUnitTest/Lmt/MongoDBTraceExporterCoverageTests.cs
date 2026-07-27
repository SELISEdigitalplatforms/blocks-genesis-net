using Blocks.Genesis;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using SeliseBlocks.LMT.Client;
using System.Reflection;

namespace XUnitTest.Lmt;

public class MongoDBTraceExporterCoverageTests
{
    private const string MongoConnectionString = "mongodb://127.0.0.1:27017";
    private const string UnreachableConnectionString = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=200&connectTimeoutMS=200";

    [Fact]
    public async Task FlushBatchAsync_ShouldPersistQueuedTraces_ToMongo_WhenNoServiceBusIsConfigured()
    {
        var previousServiceBus = Environment.GetEnvironmentVariable("ServiceBusConnectionString");
        var tenantId = $"trace-exp-{Guid.NewGuid():N}";
        var traceDatabase = new MongoClient(MongoConnectionString).GetDatabase(LmtConfiguration.TraceDatabaseName);
        try
        {
            Environment.SetEnvironmentVariable("ServiceBusConnectionString", null);

            using var exporter = new MongoDBTraceExporter("svc-trace", batchSize: 1000, blocksSecret: MakeSecret(MongoConnectionString));
            Enqueue(exporter, MakeTrace(tenantId));
            Enqueue(exporter, MakeTrace(tenantId));

            await InvokeFlush(exporter);

            var count = traceDatabase.GetCollection<BsonDocument>(tenantId).CountDocuments(FilterDefinition<BsonDocument>.Empty);
            Assert.Equal(2, count);
        }
        finally
        {
            traceDatabase.DropCollection(tenantId);
            Environment.SetEnvironmentVariable("ServiceBusConnectionString", previousServiceBus);
        }
    }

    [Fact]
    public async Task FlushBatchAsync_ShouldReturnEarly_WhenQueueIsEmpty()
    {
        var previousServiceBus = Environment.GetEnvironmentVariable("ServiceBusConnectionString");
        try
        {
            Environment.SetEnvironmentVariable("ServiceBusConnectionString", null);

            using var exporter = new MongoDBTraceExporter("svc-trace", blocksSecret: MakeSecret(MongoConnectionString));

            var exception = await Record.ExceptionAsync(() => InvokeFlush(exporter));

            Assert.Null(exception);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ServiceBusConnectionString", previousServiceBus);
        }
    }

    [Fact]
    public async Task SaveToMongoDBAsync_ShouldSwallowInsertFailures()
    {
        var previousServiceBus = Environment.GetEnvironmentVariable("ServiceBusConnectionString");
        try
        {
            Environment.SetEnvironmentVariable("ServiceBusConnectionString", null);

            using var exporter = new MongoDBTraceExporter("svc-trace", blocksSecret: MakeSecret(UnreachableConnectionString));
            var batches = new Dictionary<string, List<TraceData>> { ["tenant-x"] = [MakeTrace("tenant-x")] };

            var exception = await Record.ExceptionAsync(() => exporter.SaveToMongoDBAsync(batches));

            Assert.Null(exception);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ServiceBusConnectionString", previousServiceBus);
        }
    }

    [Fact]
    public void Dispose_ShouldFlushPendingTraces_AndBeIdempotent()
    {
        var previousServiceBus = Environment.GetEnvironmentVariable("ServiceBusConnectionString");
        var tenantId = $"trace-disp-{Guid.NewGuid():N}";
        var traceDatabase = new MongoClient(MongoConnectionString).GetDatabase(LmtConfiguration.TraceDatabaseName);
        try
        {
            Environment.SetEnvironmentVariable("ServiceBusConnectionString", null);

            var exporter = new MongoDBTraceExporter("svc-trace", blocksSecret: MakeSecret(MongoConnectionString));
            Enqueue(exporter, MakeTrace(tenantId));

            exporter.Dispose();
            var second = Record.Exception(exporter.Dispose);

            Assert.Null(second);
            var count = traceDatabase.GetCollection<BsonDocument>(tenantId).CountDocuments(FilterDefinition<BsonDocument>.Empty);
            Assert.Equal(1, count);
        }
        finally
        {
            traceDatabase.DropCollection(tenantId);
            Environment.SetEnvironmentVariable("ServiceBusConnectionString", previousServiceBus);
        }
    }

    private static IBlocksSecret MakeSecret(string traceConnectionString)
    {
        var secret = new Mock<IBlocksSecret>();
        secret.SetupGet(s => s.TraceConnectionString).Returns(traceConnectionString);
        return secret.Object;
    }

    private static TraceData MakeTrace(string tenantId)
    {
        return new TraceData
        {
            Timestamp = DateTime.UtcNow,
            TraceId = Guid.NewGuid().ToString("n"),
            SpanId = "span",
            ParentSpanId = "parent-span",
            ParentId = "parent",
            Kind = "Internal",
            ActivitySourceName = "source",
            OperationName = "op",
            StartTime = DateTime.UtcNow,
            EndTime = DateTime.UtcNow,
            Duration = 1.5,
            Status = "Ok",
            StatusDescription = string.Empty,
            ServiceName = "svc-trace",
            TenantId = tenantId
        };
    }

    private static void Enqueue(MongoDBTraceExporter exporter, TraceData trace)
    {
        var field = typeof(MongoDBTraceExporter).GetField("_batch", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        var queue = (System.Collections.Concurrent.ConcurrentQueue<TraceData>)field!.GetValue(exporter)!;
        queue.Enqueue(trace);
    }

    private static async Task InvokeFlush(MongoDBTraceExporter exporter)
    {
        var method = typeof(MongoDBTraceExporter).GetMethod("FlushBatchAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        await (Task)method!.Invoke(exporter, [])!;
    }
}
