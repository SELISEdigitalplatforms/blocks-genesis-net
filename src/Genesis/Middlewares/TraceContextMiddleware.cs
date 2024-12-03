using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using System.Diagnostics;
using System.Text.Json;

namespace Blocks.Genesis
{
    public class TraceContextMiddleware
    {
        private readonly RequestDelegate _next;

        public TraceContextMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var activity = Activity.Current;

            context.Request.Headers.TryGetValue(BlocksConstants.BlocksKey, out StringValues tenantId);

            activity.SetCustomProperty("TenantId", tenantId);

            activity.SetCustomProperty("Request", JsonSerializer.Serialize(new
            {
                Url = context.Request.Path.ToString()
            }));


            await _next(context);

            activity.SetCustomProperty("Response", JsonSerializer.Serialize(new
            {
                StatusCode = context.Response.StatusCode
            }));
        }
    }
}
