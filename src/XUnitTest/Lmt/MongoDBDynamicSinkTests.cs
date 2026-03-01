using MongoDB.Bson;
using Moq;
using SeliseBlocks.LMT.Client;
using System.Reflection;

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
    }
}
