using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using System.Security.Claims;

namespace Blocks.Genesis
{
    public static class TokenHelper
    {
        public static (string Token, bool IsThirdPartyToken) GetToken(HttpRequest request, ITenants tenants)
        {
            if (request.Headers.TryGetValue(BlocksConstants.AuthorizationHeaderName, out StringValues token) && !StringValues.IsNullOrEmpty(token) && token.ToString().StartsWith(BlocksConstants.Bearer, StringComparison.OrdinalIgnoreCase))
            {
                return (token.ToString().Substring(BlocksConstants.Bearer.Length).Trim(), false);
            }

            return GetTokenFromCookie(request, tenants);
        }

        public static (string Token, bool IsThirdPartyToken) GetTokenFromCookie(HttpRequest request, ITenants tenants)
        {
            var bc = BlocksContext.GetContext();

            var blocksToken = request.Cookies.TryGetValue($"access_token_{bc.TenantId}", out string token);

            if (blocksToken)
                return (token, false);

           var tenant = tenants.GetTenantByID(bc.TenantId);
           request.Cookies.TryGetValue(tenant?.ThirdPartyJwtTokenParameters?.CookieKey?? "", out string thirdPartyToken);


           return (thirdPartyToken, !string.IsNullOrWhiteSpace(thirdPartyToken)? true: false);
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
