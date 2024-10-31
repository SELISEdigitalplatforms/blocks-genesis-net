using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using System.Security.Claims;

namespace Blocks.Genesis
{
    public static class TokenHelper
    {
        public static string GetToken(HttpRequest request)
        {
            if (request.Headers.TryGetValue(BlocksConstants.AuthorizationHeaderName, out StringValues token) && !StringValues.IsNullOrEmpty(token))
            {
                if (token.ToString().StartsWith(BlocksConstants.Bearer, StringComparison.OrdinalIgnoreCase))
                {
                    return token.ToString().Substring(BlocksConstants.Bearer.Length).Trim();
                }
            }

            return GetTokenFromCookie(request);
        }

        private static string GetTokenFromCookie(HttpRequest request)
        {
            var originHost = GetHostOfRequestOrigin(request);
            if (string.IsNullOrEmpty(originHost) || !request.Cookies.TryGetValue(originHost, out string token))
            {
                return string.Empty;
            }

            return token;
        }

        public static string GetHostOfRequestOrigin(HttpRequest request)
        {
            if (request.Headers.TryGetValue("Origin", out StringValues origin) ||
                request.Headers.TryGetValue("Referer", out origin))
            {
                if (Uri.TryCreate(origin.ToString(), UriKind.Absolute, out Uri uri))
                {
                    return uri.Host;
                }
            }

            return string.Empty;
        }

        public static void HandleTokenIssuer(ClaimsIdentity claimsIdentity, string requestUri, string jwtBearerToken)
        {
            if (claimsIdentity == null) throw new ArgumentNullException(nameof(claimsIdentity));

            claimsIdentity.AddClaims(new[]
            {
                new Claim(BlocksContext.REQUEST_URI_CLAIM, requestUri),
                new Claim(BlocksContext.OAUTH_TOKEN_CLAIM, jwtBearerToken)
            });
        }

    }
}
