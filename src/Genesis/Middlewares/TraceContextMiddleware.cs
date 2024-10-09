using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;
using System.Diagnostics;

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

            // Capture TenantId from headers
            context.Request.Headers.TryGetValue(BlocksConstants.BlocksKey, out StringValues tenantId);

            // TenantId is most important perameter, without this we cannot store the trace
            activity.SetCustomProperty("TenantId", tenantId);

            // Capture Request details: URL and Headers
            var requestInfo = new
            {
                Url = context.Request.Path.ToString(),
                Headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString())
            };
            activity.SetCustomProperty("Request", JsonConvert.SerializeObject(requestInfo));

            // Process the request
            await _next(context);

            // Capture Response details: Status code and Headers
            var response = new
            {
                StatusCode = context.Response.StatusCode,
                Headers = context.Response.Headers.ToDictionary(h => h.Key, h => h.Value.ToString())
            };
            activity.SetCustomProperty("Response", JsonConvert.SerializeObject(response));
        }
    }
}
