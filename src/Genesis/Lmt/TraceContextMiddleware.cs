using Microsoft.AspNetCore.Http;
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
            var activity = new Activity("ProcessingRequest");
            activity.Start();

            try
            {
                await _next(context);
            }
            finally
            {
                activity.Stop();
            }
        }
    }

}
