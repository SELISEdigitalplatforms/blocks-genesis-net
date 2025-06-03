using Microsoft.AspNetCore.Http;
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

            activity.SetTag("Request.Url", context.Request.Path.ToString());
            activity.SetTag("Request.Headers", JsonSerializer.Serialize(context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString())));
            activity.SetTag("Request.Query", JsonSerializer.Serialize(context.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString())));
            activity.SetTag("Request.ContentType", context.Request.ContentType ?? "No Content Type");
            activity.SetTag("Request.Host", context.Request.Host.ToString());
            activity.SetTag("Request.Scheme", context.Request.Scheme);
            activity.SetTag("Request.Protocol", context.Request.Protocol);
            activity.SetTag("Request.Method", context.Request.Method);

            await _next(context);

            activity.SetTag("Response.StatusCode", context.Response.StatusCode);
            activity.SetTag("Response.Headers", JsonSerializer.Serialize(context.Response.Headers.ToDictionary(h => h.Key, h => h.Value.ToString())));
            activity.SetTag("Response.ContentType", context.Response.ContentType ?? "No Content Type");
            activity.SetTag("Response.Scheme", context.Request.Scheme);
            activity.SetTag("Response.Host", context.Request.Host.ToString());
            activity.SetTag("Response.IsCompleted", context.Response.HasStarted ? "Yes" : "No");


            BlocksContext.ClearContext();
        }
    }
}
