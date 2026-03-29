using Microsoft.AspNetCore.Http;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;

namespace Blocks.Genesis
{
    internal static class TenantContextHelper
    {
        public static string? ResolveTenantId(HttpRequest request, string? token = null)
        {
            request.Headers.TryGetValue(BlocksConstants.BlocksKey, out var headerTenantId);
            var tenantId = headerTenantId.ToString();
            if (!string.IsNullOrWhiteSpace(tenantId))
            {
                return tenantId;
            }

            request.Query.TryGetValue(BlocksConstants.BlocksKey, out var queryTenantId);
            tenantId = queryTenantId.ToString();
            if (!string.IsNullOrWhiteSpace(tenantId))
            {
                return tenantId;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            try
            {
                var jwtToken = new JwtSecurityTokenHandler().ReadJwtToken(token);
                tenantId = jwtToken.Claims.FirstOrDefault(c => c.Type == BlocksContext.TENANT_ID_CLAIM)?.Value;
                return string.IsNullOrWhiteSpace(tenantId) ? null : tenantId;
            }
            catch
            {
                return null;
            }
        }

        public static void EnsureTenantContext(HttpContext context, string? tenantId)
        {
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                return;
            }

            var existing = BlocksContext.GetContext();
            if (string.Equals(existing?.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var seededContext = BlocksContext.Create(
                tenantId: tenantId,
                roles: Array.Empty<string>(),
                userId: string.Empty,
                isAuthenticated: false,
                requestUri: context.Request.Host.Value,
                organizationId: string.Empty,
                expireOn: DateTime.MinValue,
                email: string.Empty,
                permissions: Array.Empty<string>(),
                userName: string.Empty,
                phoneNumber: string.Empty,
                displayName: string.Empty,
                oauthToken: string.Empty,
                refreshToken: string.Empty,
                actualTentId: tenantId);

            BlocksContext.SetContext(seededContext, false);
        }
    }
}
