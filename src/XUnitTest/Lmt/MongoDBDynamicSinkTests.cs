using MongoDB.Bson;
using Moq;
using SeliseBlocks.LMT.Client;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;

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

        [Fact]
        public void Constructor_ShouldNotCreateMessageSender_Eagerly()
        {
            var field = typeof(MongoDBDynamicSink).GetField("_messageSender", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(field);
            Assert.Null(field!.GetValue(_sink));
        }

        [Fact]
        public void GetOrCreateMessageSender_ShouldPreferLmtConfiguration_OverSecret()
        {
            var previousLmtConnection = Environment.GetEnvironmentVariable("Lmt__ConnectionString");
            ILmtMessageSender? sender = null;

            try
            {
                InitializeLmtConfigurationProvider(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Lmt:ConnectionString"] = "amqps://guest:guest@localhost:5672"
                    })
                    .Build());

                _blocksSecretMock.Setup(x => x.LmtMessageConnectionString)
                    .Returns("Endpoint=sb://secret.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=value");

                sender = InvokeGetOrCreateMessageSender(_sink);

                Assert.IsType<LmtRabbitMqSender>(sender);
            }
            finally
            {
                Environment.SetEnvironmentVariable("Lmt__ConnectionString", previousLmtConnection);
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
                _blocksSecretMock.Setup(x => x.LmtMessageConnectionString)
                    .Returns("Endpoint=sb://secret.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=value");

                sender = InvokeGetOrCreateMessageSender(_sink);

                Assert.IsType<LmtServiceBusSender>(sender);
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
                _blocksSecretMock.Setup(x => x.LmtMessageConnectionString).Returns(string.Empty);

                var sender = InvokeGetOrCreateMessageSender(_sink);

                Assert.Null(sender);
            }
            finally
            {
                Environment.SetEnvironmentVariable("ServiceBusConnectionString", previousServiceBus);
            }
        }

        private static BsonDocument InvokeConvertToBsonDocument(LogData logData)
        {
            var method = typeof(MongoDBDynamicSink).GetMethod("ConvertToBsonDocument", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            return (BsonDocument)method!.Invoke(null, [logData])!;
        }

        private static ILmtMessageSender? InvokeGetOrCreateMessageSender(MongoDBDynamicSink sink)
        {
            var method = typeof(MongoDBDynamicSink).GetMethod("GetOrCreateMessageSender", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            return (ILmtMessageSender?)method!.Invoke(sink, null);
        }

        private static void InitializeLmtConfigurationProvider(IConfiguration configuration)
        {
            var providerType = typeof(MongoDBDynamicSink).Assembly.GetType("Blocks.Genesis.LmtConfigurationProvider")!;
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
    }
}
