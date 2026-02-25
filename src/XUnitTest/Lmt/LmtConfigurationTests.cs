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
    }
}
