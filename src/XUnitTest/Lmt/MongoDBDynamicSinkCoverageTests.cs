using Blocks.Genesis;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using SeliseBlocks.LMT.Client;
using Serilog.Events;
using Serilog.Parsing;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace XUnitTest.Lmt;

public class MongoDBDynamicSinkCoverageTests
{
    private const string MongoConnectionString = "mongodb://127.0.0.1:27017";

    [Fact]
    public async Task EmitBatchAsync_ShouldRouteToServiceBusSender_WhenSenderIsPresent()
    {
        var previousServiceBus = Environment.GetEnvironmentVariable("ServiceBusConnectionString");
        try
        {
            Environment.SetEnvironmentVariable("ServiceBusConnectionString", null);

            using var sink = new MongoDBDynamicSink("svc-sink", MakeSecret(string.Empty));
            var sender = (LmtServiceBusSender)RuntimeHelpers.GetUninitializedObject(typeof(LmtServiceBusSender));
            SetField(sink, "_serviceBusSender", sender);

            var exception = await Record.ExceptionAsync(() => sink.EmitBatchAsync([MakeLogEvent()]));

            // The uninitialized sender returns early from SendLogsAsync, so the
            // service-bus branch completes without touching Mongo.
            Assert.Null(exception);

            // Detach the uninitialized sender so disposing the sink does not
            // trip over its unconstructed internals.
            SetField(sink, "_serviceBusSender", null!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ServiceBusConnectionString", previousServiceBus);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task EmitBatchAsync_ShouldPersistToMongo_WithFilteredProperties()
    {
        var previousServiceBus = Environment.GetEnvironmentVariable("ServiceBusConnectionString");
        var serviceName = $"sink-svc-{Guid.NewGuid():N}";
        var logDatabase = new MongoClient(MongoConnectionString).GetDatabase(LmtConfiguration.LogDatabaseName);
        try
        {
            Environment.SetEnvironmentVariable("ServiceBusConnectionString", null);

            using var sink = new MongoDBDynamicSink(serviceName, MakeSecret(MongoConnectionString));

            await sink.EmitBatchAsync([MakeLogEvent()]);

            var document = logDatabase.GetCollection<BsonDocument>(serviceName)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .FirstOrDefault();
            Assert.NotNull(document);
            Assert.True(document!.Contains("TenantId"));
            Assert.True(document.Contains("TraceId"));
            Assert.True(document.Contains("SpanId"));
            Assert.False(document.Contains("Disallowed"));
            Assert.Contains("boom", document["Exception"].AsString);
        }
        finally
        {
            logDatabase.DropCollection(serviceName);
            Environment.SetEnvironmentVariable("ServiceBusConnectionString", previousServiceBus);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SaveToMongoDBAsync_ShouldConvertAllPropertyKinds_AndFallBackWhenConversionThrows()
    {
        var previousServiceBus = Environment.GetEnvironmentVariable("ServiceBusConnectionString");
        var serviceName = $"sink-conv-{Guid.NewGuid():N}";
        var logDatabase = new MongoClient(MongoConnectionString).GetDatabase(LmtConfiguration.LogDatabaseName);
        try
        {
            Environment.SetEnvironmentVariable("ServiceBusConnectionString", null);

            using var sink = new MongoDBDynamicSink(serviceName, MakeSecret(MongoConnectionString));

            var log = new LogData
            {
                Timestamp = DateTime.UtcNow,
                Level = "Information",
                Message = "msg",
                Exception = string.Empty,
                ServiceName = serviceName,
                Properties = new Dictionary<string, object>
                {
                    ["Str"] = "text",
                    ["Int"] = 5,
                    ["Long"] = 5L,
                    ["Double"] = 5.5,
                    ["Bool"] = true,
                    ["Date"] = DateTime.UtcNow,
                    ["List"] = new List<object> { "a", 1 },
                    ["Dict"] = new Dictionary<string, object> { ["inner"] = "v" },
                    ["Other"] = Guid.NewGuid(),
                    ["Throws"] = new List<object> { new ThrowingToString() }
                }
            };

            await sink.SaveToMongoDBAsync([log]);

            var document = logDatabase.GetCollection<BsonDocument>(serviceName)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .FirstOrDefault();
            Assert.NotNull(document);
            Assert.Equal("text", document!["Str"].AsString);
            Assert.True(document.Contains("Throws"));
        }
        finally
        {
            logDatabase.DropCollection(serviceName);
            Environment.SetEnvironmentVariable("ServiceBusConnectionString", previousServiceBus);
        }
    }

    [Fact]
    public void Dispose_ShouldBeIdempotent()
    {
        var previousServiceBus = Environment.GetEnvironmentVariable("ServiceBusConnectionString");
        try
        {
            Environment.SetEnvironmentVariable("ServiceBusConnectionString", null);

            var sink = new MongoDBDynamicSink("sink-dispose", MakeSecret(string.Empty));

            sink.Dispose();
            var second = Record.Exception(sink.Dispose);

            Assert.Null(second);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ServiceBusConnectionString", previousServiceBus);
        }
    }

    private static LogEvent MakeLogEvent()
    {
        var template = new MessageTemplateParser().Parse("test message");
        return new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            new InvalidOperationException("boom"),
            template,
            [
                new LogEventProperty("TenantId", new ScalarValue(DateTimeOffset.UtcNow)),
                new LogEventProperty("TraceId", new SequenceValue([new ScalarValue("t1")])),
                new LogEventProperty("SpanId", new StructureValue([new LogEventProperty("inner", new ScalarValue("v"))])),
                new LogEventProperty("Disallowed", new ScalarValue("skipped"))
            ]);
    }

    private static IBlocksSecret MakeSecret(string logConnectionString)
    {
        var secret = new Mock<IBlocksSecret>();
        secret.SetupGet(s => s.LogConnectionString).Returns(logConnectionString);
        return secret.Object;
    }

    private static void SetField(object instance, string name, object value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }

    private sealed class ThrowingToString
    {
        public override string ToString() => throw new InvalidOperationException("no string");
    }
}
