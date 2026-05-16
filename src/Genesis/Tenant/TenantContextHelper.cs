using Microsoft.AspNetCore.Http;
using System.IdentityModel.Tokens.Jwt;

namespace Blocks.Genesis
{
    internal static class TenantContextHelper
    {
        private const string TenantIdParameterName = "tenant_id";
        private static readonly string[] TenantResolutionKeys = [TenantIdParameterName, BlocksConstants.BlocksKey];

        public static async Task<string?> ResolveTenantIdAsync(HttpRequest request, string? token = null)
        {
            var tenantId = ResolveTenantIdFromHeaders(request);
            if (!string.IsNullOrWhiteSpace(tenantId))
            {
                return tenantId;
            }

            tenantId = ResolveTenantIdFromQuery(request);
            if (!string.IsNullOrWhiteSpace(tenantId))
            {
                return tenantId;
            }

            if (request.HasFormContentType)
            {
                try
                {
                    var form = await request.ReadFormAsync().ConfigureAwait(false);
                    tenantId = ResolveTenantIdFromForm(form);
                    if (!string.IsNullOrWhiteSpace(tenantId))
                    {
                        return tenantId;
                    }
                }
                catch
                {
                    // Ignore form parsing issues here and continue to token-based resolution.
                }
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

        private static string? ResolveTenantIdFromHeaders(HttpRequest request)
        {
            foreach (var key in TenantResolutionKeys)
            {
                request.Headers.TryGetValue(key, out var value);
                var tenantId = value.ToString();
                if (!string.IsNullOrWhiteSpace(tenantId))
                {
                    return tenantId;
                }
            }

            return null;
        }

        private static string? ResolveTenantIdFromQuery(HttpRequest request)
        {
            foreach (var key in TenantResolutionKeys)
            {
                request.Query.TryGetValue(key, out var value);
                var tenantId = value.ToString();
                if (!string.IsNullOrWhiteSpace(tenantId))
                {
                    return tenantId;
                }
            }

            return null;
        }

        private static string? ResolveTenantIdFromForm(IFormCollection form)
        {
            foreach (var key in TenantResolutionKeys)
            {
                if (form.TryGetValue(key, out var value))
                {
                    var tenantId = value.ToString();
                    if (!string.IsNullOrWhiteSpace(tenantId))
                    {
                        return tenantId;
                    }
                }
            }

            return null;
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
