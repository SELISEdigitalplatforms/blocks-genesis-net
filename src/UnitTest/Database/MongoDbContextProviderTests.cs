using Microsoft.Extensions.Logging;
using Moq;
using MongoDB.Driver;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using Xunit;
using MongoDB.Bson;

namespace Blocks.Genesis.Tests
{
    public class MongoDbContextProviderTests
    {
        private readonly Mock<ILogger<MongoDbContextProvider>> _loggerMock;
        private readonly Mock<ITenants> _tenantsMock;
        private readonly Mock<SecurityContext> _securityContextMock;
        private readonly MongoDbContextProvider _contextProvider;
        private readonly Dictionary<string, string> _tenantDbConnectionStrings;

        public MongoDbContextProviderTests()
        {
            _loggerMock = new Mock<ILogger<MongoDbContextProvider>>();
            _tenantsMock = new Mock<ITenants>();
            _securityContextMock = new Mock<SecurityContext>();

            _tenantDbConnectionStrings = new Dictionary<string, string>
            {
                { "Tenant1", "mongodb://localhost:27017/Tenant1" },
                { "Tenant2", "mongodb://localhost:27017/Tenant2" }
            };

            _tenantsMock.Setup(t => t.GetTenantDatabaseConnectionStrings()).Returns(_tenantDbConnectionStrings);

            _contextProvider = new MongoDbContextProvider(_loggerMock.Object, _tenantsMock.Object, _securityContextMock.Object);
        }

        [Fact]
        public void Constructor_ShouldInitializeDatabases_ForValidTenantConnections()
        {
            // Arrange & Act
            var contextProvider = new MongoDbContextProvider(_loggerMock.Object, _tenantsMock.Object, _securityContextMock.Object);

            // Assert
            foreach (var tenant in _tenantDbConnectionStrings.Keys)
            {
                var db = contextProvider.GetDatabase(tenant);
                Assert.NotNull(db);
            }
        }

        [Fact]
        public void Constructor_ShouldLogInformation_WhenTenantConnectionStringIsMissing()
        {
            // Arrange
            _tenantDbConnectionStrings["Tenant3"] = string.Empty;
            _tenantsMock.Setup(t => t.GetTenantDatabaseConnectionStrings()).Returns(_tenantDbConnectionStrings);

            // Act
            var contextProvider = new MongoDbContextProvider(_loggerMock.Object, _tenantsMock.Object, _securityContextMock.Object);

            // Assert
            _loggerMock.Verify(l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Tenant DB connection string missing for tenant")),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception, string>>((v, t) => true)), Times.Once);
        }

        [Fact]
        public void Constructor_ShouldLogInformation_WhenExceptionOccurs()
        {
            // Arrange
            _tenantDbConnectionStrings["Tenant1"] = "invalid-connection-string";
            _tenantsMock.Setup(t => t.GetTenantDatabaseConnectionStrings()).Returns(_tenantDbConnectionStrings);

            // Act
            var contextProvider = new MongoDbContextProvider(_loggerMock.Object, _tenantsMock.Object, _securityContextMock.Object);

            // Assert
            _loggerMock.Verify(l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Unable to load tenant Data context for")),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception, string>>((v, t) => true)), Times.Once);
        }

        [Fact]
        public void GetDatabase_ShouldReturnExistingDatabase_WhenDatabaseExists()
        {
            // Arrange
            var tenantId = "Tenant1";
            var expectedDb = _contextProvider.GetDatabase(tenantId);

            // Act
            var actualDb = _contextProvider.GetDatabase(tenantId);

            // Assert
            Assert.Equal(expectedDb, actualDb);
        }

        [Fact]
        public void GetDatabase_ShouldSaveNewTenantDbConnection_WhenDatabaseDoesNotExist()
        {
            // Arrange
            var newTenantId = "NewTenant";
            var newTenantConnectionString = "mongodb://localhost:27017/NewTenant";
            _tenantsMock.Setup(t => t.GetTenantDatabaseConnectionString(newTenantId)).Returns(newTenantConnectionString);

            // Act
            var db = _contextProvider.GetDatabase(newTenantId);

            // Assert
            Assert.NotNull(db);
            Assert.Equal(newTenantId, db.DatabaseNamespace.DatabaseName);
        }

        [Fact]
        public void GetDatabase_WithConnectionString_ShouldReturnDatabase()
        {
            // Arrange
            var newTenantId = "NewTenant";
            var newTenantConnectionString = "mongodb://localhost:27017/NewTenant";

            // Act
            var db = _contextProvider.GetDatabase(newTenantConnectionString, newTenantId);

            // Assert
            Assert.NotNull(db);
            Assert.Equal(newTenantId, db.DatabaseNamespace.DatabaseName);
        }

        [Fact]
        public void GetCollection_WithDatabaseName_ShouldReturnMongoCollection()
        {
            // Arrange
            var tenantId = "Tenant1";
            var collectionName = "TestCollection";

            // Act
            var collection = _contextProvider.GetCollection<BsonDocument>(tenantId, collectionName);

            // Assert
            Assert.NotNull(collection);
            Assert.Equal(collectionName, collection.CollectionNamespace.CollectionName);
        }
    }
}
