using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Blocks.Genesis.Middlewares
{
    public class TenantValidationMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ITenants _tenants;
        private readonly SecurityContext _securityContext;

        public TenantValidationMiddleware(RequestDelegate next, ITenants tenants)
        {
            _next = next;
            _tenants = tenants;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            bool apiKeyExist = context.Request.Headers.TryGetValue(BlocksConstants.BlocksKey, out StringValues key);
            string? origin = context.Request.Headers.Origin;

            if (!apiKeyExist)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Forbidden: Missing_Blocks_Key");
            }

            var tenant = _tenants.GetTenantByID(key.ToString());

            if (tenant == null)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Forbidden: Not_Allowed");
            }

            if (!string.IsNullOrWhiteSpace(origin) && !tenant.ApplicationDomain.Equals(origin, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status406NotAcceptable;
                await context.Response.WriteAsync("NotAcceptable: Invalid_Origin");
            }

            SecurityContext.CreateFromTuple((tenant.TenantId, Array.Empty<string>(), string.Empty, string.Empty, true, tenant.ApplicationDomain, string.Empty));

            await _next(context);
        }

    }
}
