using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using System.Security.Claims;

namespace Blocks.Genesis
{
    public static class TokenHelper
    {
        public static string GetToken(HttpRequest request)
        {
            if (request.Headers.TryGetValue(BlocksConstants.AuthorizationHeaderName, out StringValues token) && !StringValues.IsNullOrEmpty(token) && token.ToString().StartsWith(BlocksConstants.Bearer, StringComparison.OrdinalIgnoreCase))
            {
                return token.ToString().Substring(BlocksConstants.Bearer.Length).Trim();
            }

            return GetTokenFromCookie(request);
        }

        public static string GetTokenFromCookie(HttpRequest request)
        {
            var bc = BlocksContext.GetContext();
            if (!request.Cookies.TryGetValue($"access_token_{bc.TenantId}", out string token))
            {
                return string.Empty;
            }

            return token;
        }

        public static void HandleTokenIssuer(ClaimsIdentity claimsIdentity, string requestUri)
        {
            ArgumentNullException.ThrowIfNull(claimsIdentity);

            claimsIdentity.AddClaims(
            [
                new Claim(BlocksContext.REQUEST_URI_CLAIM, requestUri)
            ]);
        }
    }
}
