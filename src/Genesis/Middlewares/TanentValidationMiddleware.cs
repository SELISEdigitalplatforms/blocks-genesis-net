using Azure.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Blocks.Genesis.Middlewares
{
    public class TanentValidationMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ICacheClient _cacheClient;
        private readonly ITenants _tenants;

        public TanentValidationMiddleware(RequestDelegate next, ICacheClient cacheClient, ITenants tenants)
        {
            _next = next;
            _cacheClient = cacheClient;
            _tenants = tenants;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            await ValidateRequestAsync(context);

            if (_cacheClient.KeyExists("CacheUpdated"))
            {
                await Task.WhenAll(_tenants.ReloadTenantsAsync(), _cacheClient.RemoveKeyAsync("CacheUpdated"));
            }

            context.Request.Headers.TryGetValue("X-Blocks-Secret", out StringValues secret);
            string cacheOriginName = await _cacheClient.GetStringValueAsync($"tenant-origin-{secret}");

            if (!context.Request.Headers.Origin.Contains(cacheOriginName))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Forbidden: Invalid_Origin");
            }

            await _next(context);
        }

        private async Task ValidateRequestAsync(HttpContext context)
        {
            bool apiSecretExist = context.Request.Headers.TryGetValue("X-Blocks-Secret", out StringValues secret);
            string? origin = context.Request.Headers.Origin;

            if (!apiSecretExist)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Forbidden: Api-Secret_Not_Exist");
            }

            if (string.IsNullOrWhiteSpace(origin))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Forbidden: Request_Origi_Not_Exist");
            }
        }
    }
}
