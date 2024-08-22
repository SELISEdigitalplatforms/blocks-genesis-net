using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Moq;
using System;
using System.Collections.Generic;
using System.Security.Claims;
using Xunit;

namespace Blocks.Genesis.Tests
{
    public class TokenHelperTests
    {
        private readonly Mock<HttpRequest> _httpRequestMock;

        public TokenHelperTests()
        {
            _httpRequestMock = new Mock<HttpRequest>();
        }

        [Fact]
        public void GetToken_ShouldReturnEmptyString_WhenAuthorizationHeaderIsMissing()
        {
            // Arrange
            _httpRequestMock.Setup(r => r.Headers["Authorization"]).Returns(StringValues.Empty);

            // Act
            var token = TokenHelper.GetToken(_httpRequestMock.Object);

            // Assert
            Assert.Equal(string.Empty, token);
        }

        [Fact]
        public void GetTokenFromCookie_ShouldReturnEmptyString_WhenCookieIsMissing()
        {
            // Arrange
            var originHost = "example.com";
            _httpRequestMock.Setup(r => r.Headers["Origin"]).Returns(new StringValues($"https://{originHost}"));
            _httpRequestMock.Setup(r => r.Cookies.TryGetValue(originHost, out It.Ref<string>.IsAny)).Returns(false);

            // Act
            var token = TokenHelper.GetToken(_httpRequestMock.Object);

            // Assert
            Assert.Equal(string.Empty, token);
        }

        [Fact]
        public void GetHostOfRequestOrigin_ShouldReturnEmptyString_WhenNoOriginOrRefererExists()
        {
            // Arrange
            _httpRequestMock.Setup(r => r.Headers["Origin"]).Returns(StringValues.Empty);
            _httpRequestMock.Setup(r => r.Headers["Referer"]).Returns(StringValues.Empty);

            // Act
            var host = TokenHelper.GetHostOfRequestOrigin(_httpRequestMock.Object);

            // Assert
            Assert.Equal(string.Empty, host);
        }

        [Fact]
        public void HandleTokenIssuer_ShouldAddClaims_WhenValidClaimsIdentityIsProvided()
        {
            // Arrange
            var claimsIdentity = new ClaimsIdentity();
            var requestUri = "/api/test";
            var jwtBearerToken = "myJwtToken";

            // Act
            TokenHelper.HandleTokenIssuer(claimsIdentity, requestUri, jwtBearerToken);

            // Assert
            Assert.Contains(claimsIdentity.Claims, c => c.Type == "requestUri" && c.Value == requestUri);
            Assert.Contains(claimsIdentity.Claims, c => c.Type == "oauthBearerToken" && c.Value == jwtBearerToken);
        }

        [Fact]
        public void HandleTokenIssuer_ShouldThrowArgumentNullException_WhenClaimsIdentityIsNull()
        {
            // Arrange
            ClaimsIdentity claimsIdentity = null;
            var requestUri = "/api/test";
            var jwtBearerToken = "myJwtToken";

            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => TokenHelper.HandleTokenIssuer(claimsIdentity, requestUri, jwtBearerToken));
        }

        [Fact]
        public void GetBlocksSecret_ShouldReturnEmptyString_WhenXBlocksSecretHeaderIsMissing()
        {
            // Arrange
            _httpRequestMock.Setup(r => r.Headers["X-Blocks-Secret"]).Returns(StringValues.Empty);

            // Act
            var secret = TokenHelper.GetBlocksSecret(_httpRequestMock.Object);

            // Assert
            Assert.Equal(string.Empty, secret);
        }

        [Fact]
        public void GetOriginOrReferer_ShouldReturnEmptyString_WhenNoOriginOrRefererExists()
        {
            // Arrange
            _httpRequestMock.Setup(r => r.Headers["Origin"]).Returns(StringValues.Empty);
            _httpRequestMock.Setup(r => r.Headers["Referer"]).Returns(StringValues.Empty);

            // Act
            var originOrReferer = TokenHelper.GetOriginOrReferer(_httpRequestMock.Object);

            // Assert
            Assert.Equal(string.Empty, originOrReferer);
        }
    }
}
