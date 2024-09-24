using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;
using System.Diagnostics;

namespace Blocks.Genesis.Middlewares
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
            var activity = new Activity("ProcessingRequest");
            activity.Start();

            try
            {
                // Capture TenantId from headers
                context.Request.Headers.TryGetValue(BlocksConstants.BlocksKey, out StringValues tenantId);
                activity.SetCustomProperty("TenantId", tenantId);

                // Capture Request details: URL and Headers
                var requestInfo = new
                {
                    Url = context.Request.Path.ToString(),
                    Headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString())
                };
                activity.SetCustomProperty("RequestInfo", JsonConvert.SerializeObject(requestInfo));

                // Process the request
                await _next(context);

                // Capture Response details: Status code and Headers
                var responseInfo = new
                {
                    StatusCode = context.Response.StatusCode,
                    Headers = context.Response.Headers.ToDictionary(h => h.Key, h => h.Value.ToString())
                };
                activity.SetCustomProperty("ResponseInfo", JsonConvert.SerializeObject(responseInfo));
            }
            finally
            {
                activity.Stop();
            }
        }
    }
}
