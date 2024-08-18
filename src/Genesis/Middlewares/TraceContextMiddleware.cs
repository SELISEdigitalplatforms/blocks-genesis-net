using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
                await _next(context);
            }
            finally
            {
                activity.Stop();
            }
        }
    }
}
