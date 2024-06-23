using Serilog.Context;

namespace Apis
{
    public class TenantEnrichmentMiddleware
    {
        private readonly RequestDelegate _next;

        public TenantEnrichmentMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            LogContext.PushProperty("TenantId", "TenantId");
            if (context.User.Identity.IsAuthenticated)
            {
                var tenantId = context.User.FindFirst("tenant_id")?.Value;
                if (!string.IsNullOrEmpty(tenantId))
                {
                    LogContext.PushProperty("TenantId", tenantId);
                }
            }

            await _next(context);
        }
    }
}
