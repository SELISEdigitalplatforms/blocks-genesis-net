using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using Moq;
using System.Diagnostics;
using System.Text.Json;

namespace XUnitTest.Database
{
    public class MongoDbContextProviderTests
    {
        private readonly Mock<ILogger<MongoDbContextProvider>> _loggerMock;
        private readonly Mock<ITenants> _tenantsMock;
        private readonly Mock<ActivitySource> _activitySourceMock;
        private readonly MongoDbContextProvider _contextProvider;

        public MongoDbContextProviderTests()
        {
            _loggerMock = new Mock<ILogger<MongoDbContextProvider>>();
            _tenantsMock = new Mock<ITenants>();
            _activitySourceMock = new Mock<ActivitySource>();

            // Sample tenant data
            _tenantsMock.Setup(t => t.GetTenantDatabaseConnectionStrings())
                .Returns(new Dictionary<string, (string, string)>
                {
                { "tenant1", ("db1", "mongodb://localhost:27017") },
                { "tenant2", ("db2", "mongodb://localhost:27017") }
                });

            _contextProvider = new MongoDbContextProvider(
                _loggerMock.Object,
                _tenantsMock.Object,
                _activitySourceMock.Object
            );
        }

        [Fact]
        public void Constructor_ShouldInitializeDatabaseConnections()
        {
            // Verifies that initial database connections are established for each tenant
            Assert.NotNull(_contextProvider.GetDatabase("tenant1"));
            Assert.NotNull(_contextProvider.GetDatabase("tenant2"));
        }

        [Fact]
        public void GetDatabase_ShouldReturnExistingDatabase()
        {
            // Act
            var database = _contextProvider.GetDatabase("tenant1");

            // Assert
            Assert.NotNull(database);
            _loggerMock.Verify(l => l.LogInformation(
                It.Is<string>(s => s.Contains("Database connection established")),
                "tenant1", "db1"), Times.Once);
        }

        [Fact]
        public void GetDatabase_ShouldReturnNull_WhenTenantIdNotSet()
        {
            // Arrange: Set context without tenant ID
            BlocksContext.GetContext(JsonSerializer.Serialize(new { TenantId = "test" }));

            // Act & Assert
            Assert.Null(_contextProvider.GetDatabase());
        }

        [Fact]
        public void GetDatabase_ShouldReturnDatabase_WhenTenantIdIsSetInContext()
        {
            // Arrange: Set context with tenant ID
            BlocksContext.GetContext(JsonSerializer.Serialize(new { TenantId = "test" }));

            // Act
            var database = _contextProvider.GetDatabase();

            // Assert
            Assert.NotNull(database);
        }

        [Fact]
        public void GetDatabase_ShouldThrowException_WhenDbConnectionStringIsMissing()
        {
            // Arrange
            _tenantsMock.Setup(t => t.GetTenantDatabaseConnectionString("tenant3"))
                .Returns((string.Empty, string.Empty));

            // Act & Assert
            var ex = Assert.Throws<KeyNotFoundException>(() => _contextProvider.GetDatabase("tenant3"));
            Assert.Contains("Database Connection string is not found", ex.Message);
        }

        [Fact]
        public void GetCollection_ShouldReturnCollection_WhenTenantIdIsSetInContext()
        {
            // Arrange: Set tenant ID in context
            BlocksContext.GetContext(JsonSerializer.Serialize(new { TenantId = "test" }));

            // Act
            var collection = _contextProvider.GetCollection<BsonDocument>("testCollection");

            // Assert
            Assert.NotNull(collection);
        }

        [Fact]
        public void GetCollection_ShouldThrowException_WhenDatabaseIsUnavailable()
        {
            // Act & Assert
            Assert.Throws<InvalidOperationException>(() => _contextProvider.GetCollection<BsonDocument>("testCollection"));
        }

        [Fact]
        public void SaveNewTenantDbConnection_ShouldSaveDatabase_WhenNewTenantProvided()
        {
            // Arrange
            _tenantsMock.Setup(t => t.GetTenantDatabaseConnectionString("tenant3"))
                .Returns(("db3", "mongodb://localhost:27017"));

            // Act
            var database = _contextProvider.GetDatabase("tenant3");

            // Assert
            Assert.NotNull(database);
            _loggerMock.Verify(l => l.LogInformation(
                It.Is<string>(s => s.Contains("New database connection saved")),
                "tenant3", "db3"), Times.Once);
        }

        [Fact]
        public void SaveNewTenantDbConnection_ShouldLogError_WhenExceptionOccurs()
        {
            // Arrange: Mock an exception when getting the connection string
            _tenantsMock.Setup(t => t.GetTenantDatabaseConnectionString("tenant3"))
                .Throws(new Exception("Mock exception"));

            // Act & Assert
            Assert.Throws<Exception>(() => _contextProvider.GetDatabase("tenant3"));
            _loggerMock.Verify(l => l.LogError(
                It.IsAny<Exception>(),
                "Error while saving new tenant DB connection for tenant: {tenantId}",
                "tenant3"), Times.Once);
        }
    }
}



