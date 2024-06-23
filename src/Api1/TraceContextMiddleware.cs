using System.Diagnostics;

namespace Api1
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
