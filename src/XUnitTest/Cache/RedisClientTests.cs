using Blocks.Genesis;
using Moq;
using StackExchange.Redis;
using System.Diagnostics;

namespace XUnitTest.Cache
{
    public class RedisClientTests
    {
        private readonly Mock<IDatabase> _mockDatabase;
        private readonly Mock<IBlocksSecret> _mockSecret;
        private readonly Mock<ActivitySource> _mockActivitySource;
        private readonly RedisClient _redisClient;

        public RedisClientTests()
        {
            _mockDatabase = new Mock<IDatabase>();
            _mockSecret = new Mock<IBlocksSecret>();
            _mockActivitySource = new Mock<ActivitySource>("TestActivitySource");

            _mockSecret.Setup(s => s.CacheConnectionString).Returns("localhost");
            var mockConnection = new Mock<IConnectionMultiplexer>();
            mockConnection.Setup(c => c.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_mockDatabase.Object);

            _redisClient = new RedisClient(_mockSecret.Object, _mockActivitySource.Object);
        }

        [Fact]
        public void KeyExists_ShouldReturnTrue_WhenKeyExists()
        {
            // Arrange
            var key = "existingKey";
            _mockDatabase.Setup(db => db.KeyExists(key, CommandFlags.None)).Returns(true);

            // Act
            var result = _redisClient.KeyExists(key);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public async Task KeyExistsAsync_ShouldReturnTrue_WhenKeyExistsAsync()
        {
            // Arrange
            var key = "existingKey";
            _mockDatabase.Setup(db => db.KeyExistsAsync(key, CommandFlags.None)).ReturnsAsync(true);

            // Act
            var result = await _redisClient.KeyExistsAsync(key);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void AddStringValue_ShouldReturnTrue_WhenStringSetSuccessfully()
        {
            // Arrange
            var key = "newKey";
            var value = "someValue";
            _mockDatabase.Setup(db => db.StringSet(key, value, null, When.Always, CommandFlags.None)).Returns(true);

            // Act
            var result = _redisClient.AddStringValue(key, value);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public async Task AddStringValueAsync_ShouldReturnTrue_WhenStringSetSuccessfullyAsync()
        {
            // Arrange
            var key = "newKey";
            var value = "someValue";
            _mockDatabase.Setup(db => db.StringSetAsync(key, value, null, When.Always, CommandFlags.None)).ReturnsAsync(true);

            // Act
            var result = await _redisClient.AddStringValueAsync(key, value);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void GetStringValue_ShouldReturnExpectedValue_WhenKeyExists()
        {
            // Arrange
            var key = "existingKey";
            var expectedValue = "expectedValue";
            _mockDatabase.Setup(db => db.StringGet(key, CommandFlags.None)).Returns(expectedValue);

            // Act
            var result = _redisClient.GetStringValue(key);

            // Assert
            Assert.Equal(expectedValue, result);
        }

        [Fact]
        public async Task GetStringValueAsync_ShouldReturnExpectedValue_WhenKeyExistsAsync()
        {
            // Arrange
            var key = "existingKey";
            var expectedValue = "expectedValue";
            _mockDatabase.Setup(db => db.StringGetAsync(key, CommandFlags.None)).ReturnsAsync(expectedValue);

            // Act
            var result = await _redisClient.GetStringValueAsync(key);

            // Assert
            Assert.Equal(expectedValue, result);
        }

        [Fact]
        public void RemoveKey_ShouldReturnTrue_WhenKeyDeleted()
        {
            // Arrange
            var key = "existingKey";
            _mockDatabase.Setup(db => db.KeyDelete(key, CommandFlags.None)).Returns(true);

            // Act
            var result = _redisClient.RemoveKey(key);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public async Task RemoveKeyAsync_ShouldReturnTrue_WhenKeyDeletedAsync()
        {
            // Arrange
            var key = "existingKey";
            _mockDatabase.Setup(db => db.KeyDeleteAsync(key, CommandFlags.None)).ReturnsAsync(true);

            // Act
            var result = await _redisClient.RemoveKeyAsync(key);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void AddHashValue_ShouldReturnTrue_WhenHashSetSuccessfully()
        {
            // Arrange
            var key = "hashKey";
            var hashEntries = new[] { new HashEntry("field1", "value1") };
            _mockDatabase.Setup(db => db.HashSet(key, hashEntries, CommandFlags.None));

            // Act
            var result = _redisClient.AddHashValue(key, hashEntries);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public async Task AddHashValueAsync_ShouldReturnTrue_WhenHashSetSuccessfullyAsync()
        {
            // Arrange
            var key = "hashKey";
            var hashEntries = new[] { new HashEntry("field1", "value1") };
            _mockDatabase.Setup(db => db.HashSetAsync(key, hashEntries, CommandFlags.None)).Returns(Task.CompletedTask);

            // Act
            var result = await _redisClient.AddHashValueAsync(key, hashEntries);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void GetHashValue_ShouldReturnExpectedHashEntries_WhenKeyExists()
        {
            // Arrange
            var key = "hashKey";
            var expectedEntries = new[] { new HashEntry("field1", "value1") };
            _mockDatabase.Setup(db => db.HashGetAll(key, CommandFlags.None)).Returns(expectedEntries);

            // Act
            var result = _redisClient.GetHashValue(key);

            // Assert
            Assert.Equal(expectedEntries, result);
        }

        [Fact]
        public async Task GetHashValueAsync_ShouldReturnExpectedHashEntries_WhenKeyExistsAsync()
        {
            // Arrange
            var key = "hashKey";
            var expectedEntries = new[] { new HashEntry("field1", "value1") };
            _mockDatabase.Setup(db => db.HashGetAllAsync(key, CommandFlags.None)).ReturnsAsync(expectedEntries);

            // Act
            var result = await _redisClient.GetHashValueAsync(key);

            // Assert
            Assert.Equal(expectedEntries, result);
        }
    }

}



