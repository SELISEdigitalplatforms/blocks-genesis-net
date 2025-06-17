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

        public static string GetTokenFromCookie(HttpRequest request)
        {
            if (!request.Cookies.TryGetValue("access_token", out string token))
            {
                return string.Empty;
            }

            return token;
        }

        public static void HandleTokenIssuer(ClaimsIdentity claimsIdentity, string requestUri)
        {
            if (claimsIdentity == null) throw new ArgumentNullException(nameof(claimsIdentity));

            claimsIdentity.AddClaims(new[]
            {
                new Claim(BlocksContext.REQUEST_URI_CLAIM, requestUri)
            });
        }

    }
}
