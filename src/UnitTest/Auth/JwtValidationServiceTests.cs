using Moq;
using StackExchange.Redis;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Blocks.Genesis.Tests
{
    public class JwtValidationServiceTests
    {
        private readonly Mock<IDatabase> _redisDbMock;
        private readonly JwtValidationService _jwtValidationService;

        public JwtValidationServiceTests()
        {
            _redisDbMock = new Mock<IDatabase>();
            var cacheClientMock = new Mock<ICacheClient>();
            cacheClientMock.Setup(c => c.CacheDatabase()).Returns(_redisDbMock.Object);
            _jwtValidationService = new JwtValidationService(cacheClientMock.Object);
        }

        [Fact]
        public void CreateSecurityKey_ShouldReturnNull_WhenInvalidPathProvided()
        {
            // Arrange
            var signingKeyPath = "/invalid/path";
            var signingKeyPassword = "password";

            // Act
            var result = JwtValidationService.CreateSecurityKey(signingKeyPath, signingKeyPassword);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public void GetTokenParameters_ShouldThrowKeyNotFoundException_WhenTenantDoesNotExist()
        {
            // Arrange
            var tenantId = "tenant1";
            _redisDbMock.Setup(db => db.HashGetAll(It.IsAny<RedisKey>(), CommandFlags.None)).Returns(new HashEntry[0]);

            // Act & Assert
            Assert.Throws<KeyNotFoundException>(() => _jwtValidationService.GetTokenParameters(tenantId));
        }
    }
}
