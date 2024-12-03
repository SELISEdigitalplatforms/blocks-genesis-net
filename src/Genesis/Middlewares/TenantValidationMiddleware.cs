using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using System.Diagnostics;
using System.Text.Json;

namespace Blocks.Genesis
{
    public class TenantValidationMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ITenants _tenants;

        public TenantValidationMiddleware(RequestDelegate next, ITenants tenants)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!context.Request.Headers.TryGetValue(BlocksConstants.BlocksKey, out var apiKey) || StringValues.IsNullOrEmpty(apiKey))
            {
                await RejectRequest(context, StatusCodes.Status403Forbidden, "Forbidden: Missing_Blocks_Key");
                return;
            }

            var tenant = _tenants.GetTenantByID(apiKey.ToString());
            if (tenant == null)
            {
                await RejectRequest(context, StatusCodes.Status403Forbidden, "Forbidden: Not_Allowed");
                return;
            }

            if (!IsValidOrigin(context.Request.Headers.Origin, tenant.ApplicationDomain))
            {
                await RejectRequest(context, StatusCodes.Status406NotAcceptable, "NotAcceptable: Invalid_Origin");
                return;
            }

            AttachTenantDataToActivity(tenant);

            await _next(context);
        }

        private static bool IsValidOrigin(string? originHeader, string applicationDomain)
        {
            if (string.IsNullOrWhiteSpace(originHeader)) return true;

            try
            {
                // Parse the origin to extract hostname
                var originUri = new Uri(originHeader);
                var originHost = originUri.Host;

                // Normalize application domain (remove protocol if present)
                var normalizedDomain = applicationDomain.Replace("http://", "").Replace("https://", "").Split(":")[0];

                // Allow requests from localhost during development
                if (originHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;

                // Compare origin host with normalized application domain
                return string.Equals(originHost, normalizedDomain, StringComparison.OrdinalIgnoreCase);
            }
            catch (UriFormatException)
            {
                return false; // Invalid origin format
            }
        }

        private static Task RejectRequest(HttpContext context, int statusCode, string message)
        {
            context.Response.StatusCode = statusCode;
            return context.Response.WriteAsync(message);
        }

        private static void AttachTenantDataToActivity(Tenant tenant)
        {
            if (Activity.Current == null) return;

            var securityData = BlocksContext.CreateFromTuple((
                tenant.TenantId,
                Array.Empty<string>(),
                string.Empty,
                false,
                tenant.ApplicationDomain,
                string.Empty,
                DateTime.MinValue,
                string.Empty,
                Array.Empty<string>(),
                string.Empty
            ));

            Activity.Current.SetCustomProperty("SecurityContext", JsonSerializer.Serialize(securityData));
        }
    }
}
