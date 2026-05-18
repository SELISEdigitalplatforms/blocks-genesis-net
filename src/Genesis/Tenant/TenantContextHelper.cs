using Microsoft.AspNetCore.Http;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text.Json;

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

        public static string? ResolveTenantIdFromHeaders(HttpRequest request)
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

        public static string? ResolveTenantIdFromQuery(HttpRequest request)
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

        public static string? ResolveTenantIdFromForm(IFormCollection form)
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

        public static string NormalizeDomain(string domain) => BlocksContext.NormalizeDomain(domain);

        public static bool IsLocalhostHost(string? host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return IPAddress.TryParse(host, out var ipAddress) && IPAddress.IsLoopback(ipAddress);
        }

        public static bool IsLocalhostHeader(string? headerValue)
        {
            if (string.IsNullOrWhiteSpace(headerValue))
            {
                return false;
            }

            if (!Uri.TryCreate(headerValue, UriKind.Absolute, out var uri))
            {
                return false;
            }

            return IsLocalhostHost(uri.Host);
        }

        public static bool IsDomainAllowed(string? headerValue, Tenant tenant)
        {
            if (string.IsNullOrWhiteSpace(headerValue)) return true;

            try
            {
                var uri = new Uri(headerValue);
                var host = NormalizeDomain(uri.Host);

                var allowedDomains = tenant.Applications?
                    .Select(a => NormalizeDomain(a.Domain))
                    .Where(d => !string.IsNullOrWhiteSpace(d))
                    ?? [];

                return allowedDomains.Contains(host, StringComparer.OrdinalIgnoreCase);
            }
            catch (UriFormatException)
            {
                return false; // Invalid header format
            }
        }

        public static Task RejectRequest(HttpContext context, int statusCode, string message)
        {
            context.Response.StatusCode = statusCode;
            return context.Response.WriteAsync(JsonSerializer.Serialize(new BaseResponse
            {
                IsSuccess = false,
                Errors = new Dictionary<string, string> { { "Message", message } }
            }));
        }

        public static bool IsValidOriginOrReferer(string? origin, string? referer, Tenant tenant)
        {
            // Allow local development origins in tenant middleware.
            if (IsLocalhostHeader(origin) || IsLocalhostHeader(referer))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(origin) && string.IsNullOrWhiteSpace(referer))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(origin) && !IsDomainAllowed(origin, tenant))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(referer) && !IsDomainAllowed(referer, tenant))
            {
                return false;
            }

            return true;
        }

        public static string ResolveApplicationDomain(Tenant tenant, string? origin, string? referer)
        {
            var browserHost = string.Empty;

            if (!string.IsNullOrWhiteSpace(origin) && !IsLocalhostHeader(origin))
            {
                if (Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
                    browserHost = NormalizeDomain(originUri.Host);
            }
            else if (!string.IsNullOrWhiteSpace(referer) && !IsLocalhostHeader(referer))
            {
                if (Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
                    browserHost = NormalizeDomain(refererUri.Host);
            }

            // Browser call: return the exact matching domain from Applications
            if (!string.IsNullOrWhiteSpace(browserHost))
            {
                var match = tenant.Applications?
                    .FirstOrDefault(a => NormalizeDomain(a.Domain).Equals(browserHost, StringComparison.OrdinalIgnoreCase));

                if (match != null) return match.Domain;
            }

            // Non-browser call
            return string.Empty;
        }

        public static void EnsureTenantContext(HttpContext context, Tenant? tenant)
        {
            if (tenant == null)
            {
                return;
            }

            var existing = BlocksContext.GetContext();
            if (string.Equals(existing?.TenantId, tenant.TenantId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var origin = context.Request.Headers.Origin.FirstOrDefault();
            var referer = context.Request.Headers.Referer.FirstOrDefault();

            var applicationDomain = ResolveApplicationDomain(tenant, origin, referer);

            string actualTenantId = tenant.TenantId;

            var seededContext = BlocksContext.Create(
                tenantId: tenant.TenantId,
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
                actualTenantId: actualTenantId,
                applicationDomain: applicationDomain);

            BlocksContext.SetContext(seededContext, false);
        }
    }
}
