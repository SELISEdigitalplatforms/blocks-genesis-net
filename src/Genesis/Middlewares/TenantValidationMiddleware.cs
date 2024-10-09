using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;
using System.Diagnostics;

namespace Blocks.Genesis
{
    public class TenantValidationMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ITenants _tenants;

        public TenantValidationMiddleware(RequestDelegate next, ITenants tenants)
        {
            _next = next;
            _tenants = tenants;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!TryGetApiKey(context.Request.Headers, out StringValues apiKey))
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

            StoreTenantDataInActivity(tenant);

            await _next(context);
        }

        private static bool TryGetApiKey(IHeaderDictionary headers, out StringValues apiKey)
        {
            return headers.TryGetValue(BlocksConstants.BlocksKey, out apiKey);
        }

        private static bool IsValidOrigin(string? origin, string applicationDomain)
        {
            return string.IsNullOrWhiteSpace(origin) || origin.Equals(applicationDomain, StringComparison.OrdinalIgnoreCase);
        }

        private static async Task RejectRequest(HttpContext context, int statusCode, string message)
        {
            context.Response.StatusCode = statusCode;
            await context.Response.WriteAsync(message);
        }

        private static void StoreTenantDataInActivity(Tenant tenant)
        {
            var activity = Activity.Current;
            if (activity != null)
            {
                var securityData = BlocksContext.CreateFromTuple((tenant.TenantId, Array.Empty<string>(), string.Empty, false, tenant.ApplicationDomain, string.Empty));
        
                activity.SetCustomProperty("SecurityContext", JsonConvert.SerializeObject(securityData));
            }
        }
    }
}
