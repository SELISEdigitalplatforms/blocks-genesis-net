using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using System;
using System.Threading.Tasks;
using Xunit;

namespace Blocks.Genesis.Tests
{
    public class LmtConfigurationTests
    {
        private readonly Mock<IMongoDatabase> _mongoDatabaseMock;
        private readonly Mock<IMongoCollection<BsonDocument>> _mongoCollectionMock;
        private readonly Mock<IMongoClient> _mongoClientMock;

        // Use a connection string with 1ms timeouts so MongoDB operations fail immediately
        private const string FastFailConnection = "mongodb://localhost:1/?serverSelectionTimeoutMS=1&connectTimeoutMS=1&socketTimeoutMS=1";

        public LmtConfigurationTests()
        {
            _mongoDatabaseMock = new Mock<IMongoDatabase>();
            _mongoCollectionMock = new Mock<IMongoCollection<BsonDocument>>();
            _mongoClientMock = new Mock<IMongoClient>();
        }

        [Fact]
        public void GetMongoDatabase_ShouldReturnDatabaseInstance()
        {
            // Arrange
            var connectionString = "mongodb://localhost:27017";
            var databaseName = "TestDatabase";
            _mongoClientMock.Setup(client => client.GetDatabase(databaseName, null))
                            .Returns(_mongoDatabaseMock.Object);

            // Act
            var database = LmtConfiguration.GetMongoDatabase(connectionString, databaseName);

            // Assert
            Assert.NotNull(database);
        }

        [Fact]
        public void GetMongoCollection_ShouldReturnCollectionInstance()
        {
            // Arrange
            var connectionString = "mongodb://localhost:27017";
            var databaseName = "TestDatabase";
            var collectionName = "TestCollection";

            _mongoClientMock.Setup(client => client.GetDatabase(databaseName, null))
                            .Returns(_mongoDatabaseMock.Object);
            _mongoDatabaseMock.Setup(db => db.GetCollection<BsonDocument>(collectionName, null))
                              .Returns(_mongoCollectionMock.Object);

            // Act
            var collection = LmtConfiguration.GetMongoCollection<BsonDocument>(connectionString, databaseName, collectionName);

            // Assert
            Assert.NotNull(collection);
        }

        [Fact]
        public void CreateCollectionForTrace_ShouldCatchException_WhenConnectionFails()
        {
            var ex = Record.Exception(() =>
                LmtConfiguration.CreateCollectionForTrace(FastFailConnection, "test-traces"));

            Assert.Null(ex);
        }

        [Fact]
        public void CreateCollectionForMetrics_ShouldCatchException_WhenConnectionFails()
        {
            var ex = Record.Exception(() =>
                LmtConfiguration.CreateCollectionForMetrics(FastFailConnection, "test-metrics"));

            Assert.Null(ex);
        }

        [Fact]
        public void CreateCollectionForLogs_ShouldCatchException_WhenConnectionFails()
        {
            var ex = Record.Exception(() =>
                LmtConfiguration.CreateCollectionForLogs(FastFailConnection, "test-logs"));

            Assert.Null(ex);
        }

        [Fact]
        public void CreateIndex_ShouldThrow_WhenConnectionFails()
        {
            var indexKeys = new BsonDocument { { "TraceId", 1 }, { "Timestamp", -1 } };

            Assert.ThrowsAny<Exception>(() =>
                LmtConfiguration.CreateIndex(FastFailConnection, "Traces", "test-col", indexKeys));
        }

        [Fact]
        public void CreateIndex_ShouldAcceptPartialFilter()
        {
            var indexKeys = new BsonDocument { { "TenantId", 1 }, { "Timestamp", -1 } };
            var partialFilter = new BsonDocument("TenantId", new BsonDocument("$exists", true));

            Assert.ThrowsAny<Exception>(() =>
                LmtConfiguration.CreateIndex(FastFailConnection, "Logs", "test-col", indexKeys, partialFilter));
        }
    }
}
