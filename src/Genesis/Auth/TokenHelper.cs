using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using System.Security.Claims;

namespace Blocks.Genesis
{
    public static class TokenHelper
    {
        public static string GetToken(HttpRequest request)
        {
            if (request.Headers.TryGetValue("Authorization", out StringValues token) && !StringValues.IsNullOrEmpty(token))
            {
                const string bearerPrefix = "Bearer ";
                if (token.ToString().StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return token.ToString().Substring(bearerPrefix.Length).Trim();
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
            claimsIdentity.AddClaims(new[]
            {
                new Claim("requestUri", requestUri),
                new Claim("oauthBearerToken", jwtBearerToken)
            });
        }
    }
}
