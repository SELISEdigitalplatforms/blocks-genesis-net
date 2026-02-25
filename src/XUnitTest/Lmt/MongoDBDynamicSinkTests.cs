using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using Serilog.Events;
using Serilog.Parsing;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Blocks.Genesis.Tests
{
    public class MongoDBDynamicSinkTests
    {
        private readonly Mock<IBlocksSecret> _blocksSecretMock;
        private readonly Mock<IMongoCollection<BsonDocument>> _mongoCollectionMock;
        private readonly MongoDBDynamicSink _sink;

        public MongoDBDynamicSinkTests()
        {
            _blocksSecretMock = new Mock<IBlocksSecret>();
            _mongoCollectionMock = new Mock<IMongoCollection<BsonDocument>>();

            // Mock the LogConnectionString to return a fake connection string
            _blocksSecretMock.Setup(x => x.LogConnectionString).Returns("mongodb://localhost:27017");

            // Initialize MongoDBDynamicSink with mocked dependencies
            _sink = new MongoDBDynamicSink("TestService", _blocksSecretMock.Object);
        }

        [Fact]
        public void CreateLogBsonDocument_ShouldReturnCorrectBsonDocument()
        {
            // Arrange
            var logEvent = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null, new MessageTemplate("Test message", new List<MessageTemplateToken>()), new List<LogEventProperty>
            {
                new LogEventProperty("PropertyKey", new ScalarValue("PropertyValue"))
            });

            // Act
            var document = InvokeCreateLogBsonDocument(_sink, logEvent);

            // Assert
            Assert.Equal(logEvent.MessageTemplate.Text, document["MessageTemplate"].AsString);
            Assert.Equal(logEvent.Level.ToString(), document["Level"].AsString);
            Assert.Equal(logEvent.RenderMessage(), document["Message"].AsString);
            Assert.Equal("TestService", document["ServiceName"].AsString);
            Assert.Equal("PropertyValue", document["PropertyKey"].AsString);
        }

        private BsonDocument InvokeCreateLogBsonDocument(MongoDBDynamicSink sink, LogEvent logEvent)
        {
            // Use reflection to invoke the private CreateLogBsonDocument method
            var method = typeof(MongoDBDynamicSink).GetMethod("CreateLogBsonDocument", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (BsonDocument)method.Invoke(sink, new object[] { logEvent });
        }
    }
}
