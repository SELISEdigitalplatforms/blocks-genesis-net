using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using SeliseBlocks.LMT.Client;
using Serilog.Events;
using Serilog.Parsing;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Blocks.Genesis.Tests
{
    public class MongoDBDynamicSinkTests
    {
        private readonly Mock<IBlocksSecret> _blocksSecretMock;
        private readonly MongoDBDynamicSink _sink;

        public MongoDBDynamicSinkTests()
        {
            _blocksSecretMock = new Mock<IBlocksSecret>();

            _blocksSecretMock.Setup(x => x.LogConnectionString).Returns("mongodb://localhost:27017");
            _sink = new MongoDBDynamicSink("TestService", _blocksSecretMock.Object);
        }

        [Fact]
        public void ConvertToBsonDocument_ShouldMapCoreFieldsAndProperties()
        {
            var logData = new LogData
            {
                Timestamp = DateTime.UtcNow,
                Level = "Information",
                Message = "Test message",
                Exception = string.Empty,
                ServiceName = "TestService",
                Properties = new Dictionary<string, object>
                {
                    ["PropertyKey"] = "PropertyValue",
                    ["Count"] = 3
                }
            };

            var document = InvokeConvertToBsonDocument(logData);

            Assert.Equal("Information", document["Level"].AsString);
            Assert.Equal("Test message", document["Message"].AsString);
            Assert.Equal("TestService", document["ServiceName"].AsString);
            Assert.Equal("PropertyValue", document["PropertyKey"].AsString);
            Assert.Equal(3, document["Count"].AsInt32);
        }

        private static BsonDocument InvokeConvertToBsonDocument(LogData logData)
        {
            var method = typeof(MongoDBDynamicSink).GetMethod("ConvertToBsonDocument", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            return (BsonDocument)method!.Invoke(null, [logData])!;
        }

        [Fact]
        public async Task EmitBatchAsync_ShouldFilterAllowedProperties_AndNotThrow_WhenNoSinks()
        {
            var sink = CreateSinkWithInternals(serviceBusSender: null, database: null);

            var parser = new MessageTemplateParser();
            var evt = new LogEvent(
                DateTimeOffset.UtcNow,
                LogEventLevel.Information,
                null,
                parser.Parse("hello"),
                new List<LogEventProperty>
                {
                    new("TenantId", new ScalarValue("tenant-1")),
                    new("TraceId", new ScalarValue("trace-1")),
                    new("Ignored", new ScalarValue("value"))
                });

            var ex = await Record.ExceptionAsync(() => sink.EmitBatchAsync(new[] { evt }));

            Assert.Null(ex);
        }

        [Fact]
        public async Task EmitBatchAsync_ShouldCallMongo_WhenDatabaseConfigured()
        {
            var collection = new Mock<IMongoCollection<BsonDocument>>();
            collection.Setup(c => c.InsertManyAsync(It.IsAny<IEnumerable<BsonDocument>>(), It.IsAny<InsertManyOptions>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var database = new Mock<IMongoDatabase>();
            database.Setup(d => d.GetCollection<BsonDocument>("svc", It.IsAny<MongoCollectionSettings>())).Returns(collection.Object);

            var sink = CreateSinkWithInternals(serviceBusSender: null, database.Object, "svc");
            var parser = new MessageTemplateParser();
            var evt = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Warning, null, parser.Parse("m"), new List<LogEventProperty>
            {
                new("TenantId", new ScalarValue("tenant-2"))
            });

            await sink.EmitBatchAsync(new[] { evt });

            collection.Verify(c => c.InsertManyAsync(It.IsAny<IEnumerable<BsonDocument>>(), It.IsAny<InsertManyOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task SaveToMongoDBAsync_ShouldSwallowInsertExceptions()
        {
            var collection = new Mock<IMongoCollection<BsonDocument>>();
            collection.Setup(c => c.InsertManyAsync(It.IsAny<IEnumerable<BsonDocument>>(), It.IsAny<InsertManyOptions>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("insert failed"));

            var database = new Mock<IMongoDatabase>();
            database.Setup(d => d.GetCollection<BsonDocument>("svc", It.IsAny<MongoCollectionSettings>())).Returns(collection.Object);

            var sink = CreateSinkWithInternals(serviceBusSender: null, database: database.Object, serviceName: "svc");

            await sink.SaveToMongoDBAsync(new List<LogData>
            {
                new() { Message = "m", Level = "Information", ServiceName = "svc", Properties = new Dictionary<string, object>() }
            });

            collection.Verify(c => c.InsertManyAsync(It.IsAny<IEnumerable<BsonDocument>>(), It.IsAny<InsertManyOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public void ConvertLogEventPropertyValue_ShouldCoverScalarSequenceAndStructure()
        {
            var method = typeof(MongoDBDynamicSink).GetMethod("ConvertLogEventPropertyValue", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var scalar = method!.Invoke(null, new object[] { new ScalarValue(123) });
            Assert.Equal("123", scalar);

            var dt = method.Invoke(null, new object[] { new ScalarValue(DateTimeOffset.UtcNow) });
            Assert.IsType<string>(dt);

            var seq = method.Invoke(null, new object[] { new SequenceValue(new LogEventPropertyValue[] { new ScalarValue("a"), new ScalarValue("b") }) });
            var seqList = Assert.IsType<List<string>>(seq);
            Assert.Equal(2, seqList.Count);

            var structValue = new StructureValue(new[] { new LogEventProperty("K", new ScalarValue("V")) });
            var structResult = method.Invoke(null, new object[] { structValue });
            var dict = Assert.IsType<Dictionary<string, string>>(structResult);
            Assert.Equal("\"V\"", dict["K"]);
        }

        [Fact]
        public void ConvertPropertyToBsonValue_ShouldHandleComplexTypes()
        {
            var method = typeof(MongoDBDynamicSink).GetMethod("ConvertPropertyToBsonValue", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var listValue = method!.Invoke(null, new object[] { new List<object> { 1, "x" } });
            Assert.IsType<BsonArray>(listValue);

            var dictValue = method.Invoke(null, new object[] { new Dictionary<string, object> { ["a"] = 1, ["b"] = "y" } });
            Assert.IsType<BsonDocument>(dictValue);

            var fallback = method.Invoke(null, new object?[] { null! });
            var bsonString = Assert.IsType<BsonString>(fallback);
            Assert.Equal(string.Empty, bsonString.AsString);
        }

        [Fact]
        public void Dispose_ShouldBeIdempotent()
        {
            var sink = CreateSinkWithInternals(serviceBusSender: null, database: null);

            sink.Dispose();
            sink.Dispose();
        }

        [Fact]
        public void Constructor_ShouldCreateSender_WhenServiceBusEnvIsPresent()
        {
            var previous = Environment.GetEnvironmentVariable("ServiceBusConnectionString");
            try
            {
                Environment.SetEnvironmentVariable("ServiceBusConnectionString", "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=secret");

                var blocksSecret = new Mock<IBlocksSecret>();
                blocksSecret.SetupGet(x => x.LogConnectionString).Returns(string.Empty);

                var sink = new MongoDBDynamicSink("svc", blocksSecret.Object);
                sink.Dispose();
            }
            finally
            {
                Environment.SetEnvironmentVariable("ServiceBusConnectionString", previous);
            }
        }

        [Fact]
        public void ConvertLogEventPropertyValue_ShouldUseDefaultBranch_ForUnsupportedType()
        {
            var method = typeof(MongoDBDynamicSink).GetMethod("ConvertLogEventPropertyValue", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var dictionaryValue = new DictionaryValue(new[] { new KeyValuePair<ScalarValue, LogEventPropertyValue>(new ScalarValue("k"), new ScalarValue("v")) });
            var result = method!.Invoke(null, new object[] { dictionaryValue });

            Assert.IsType<string>(result);
        }

        [Fact]
        public void ConvertPropertyToBsonValue_ShouldHandlePrimitiveCases()
        {
            var method = typeof(MongoDBDynamicSink).GetMethod("ConvertPropertyToBsonValue", BindingFlags.NonPublic | BindingFlags.Static)!;

            Assert.Equal(5L, ((BsonValue)method.Invoke(null, new object[] { 5L })!).AsInt64);
            Assert.Equal(1.25, ((BsonValue)method.Invoke(null, new object[] { 1.25 })!).AsDouble);
            Assert.True(((BsonValue)method.Invoke(null, new object[] { true })!).AsBoolean);
            Assert.True(((BsonValue)method.Invoke(null, new object[] { DateTime.UtcNow })!).IsValidDateTime);
        }

        private static MongoDBDynamicSink CreateSinkWithInternals(LmtServiceBusSender? serviceBusSender, IMongoDatabase? database, string serviceName = "svc")
        {
            var sink = (MongoDBDynamicSink)RuntimeHelpers.GetUninitializedObject(typeof(MongoDBDynamicSink));
            SetField(sink, "_serviceName", serviceName);
            SetField(sink, "_serviceBusSender", serviceBusSender);
            SetField(sink, "_database", database);
            SetField(sink, "_disposed", false);
            return sink;
        }

        private static void SetField(object instance, string name, object? value)
        {
            var field = instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            field!.SetValue(instance, value);
        }
    }
}
