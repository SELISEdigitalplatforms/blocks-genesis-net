using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using OpenTelemetry;
using System.Diagnostics;
using System.Text.Json;

namespace Blocks.Genesis
{
    public class TenantValidationMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ITenants _tenants;
        private readonly ICryptoService _cryptoService;

        public TenantValidationMiddleware(RequestDelegate next, ITenants tenants, ICryptoService cryptoService)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
            _cryptoService = cryptoService ?? throw new ArgumentNullException(nameof(cryptoService));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var activity = Activity.Current;

            activity.SetTag("http.headers", JsonSerializer.Serialize(context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString())));
            activity.SetTag("http.query", JsonSerializer.Serialize(context.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString())));

            context.Request.Headers.TryGetValue(BlocksConstants.BlocksKey, out var apiKey);
            bool apiKeyFoundInHeader = !StringValues.IsNullOrEmpty(apiKey);

            if (!apiKeyFoundInHeader)
            {
                context.Request.Query.TryGetValue(BlocksConstants.BlocksKey, out apiKey);
            }

            Tenant? tenant = null;

            if (StringValues.IsNullOrEmpty(apiKey))
            {
                var baseUrl = context.Request.Host.Value;

                tenant = _tenants.GetTenantByApplicationDomain(baseUrl);

                if (tenant == null)
                {
                    await RejectRequest(context, StatusCodes.Status404NotFound, "Not_Found: Application_Not_Found");
                    return;
                }
            }

            tenant ??= _tenants.GetTenantByID(apiKey.ToString());

            if (tenant == null || tenant.IsDisabled)
            {
                await RejectRequest(context, StatusCodes.Status404NotFound, "Not_Found: Application_Not_Found");
                return;
            }


            if (!IsValidOriginOrReferer(context, tenant))
            {
                await RejectRequest(context, StatusCodes.Status406NotAcceptable, "NotAcceptable: Invalid_Origin_Or_Referer");
                return;
            }

            AttachTenantDataToActivity(tenant);

            if (context.Request.ContentType == "application/grpc" && context.Request.Headers.TryGetValue(BlocksConstants.BlocksGrpcKey, out var grpcKey))
            {
                var hash = _cryptoService.Hash(apiKey, tenant.TenantSalt);
                if (hash != grpcKey)
                {
                    await RejectRequest(context, StatusCodes.Status403Forbidden, "Forbidden: Missing_Blocks_Service_Key");
                    return;
                }
            }

            await _next(context);

            activity.SetTag("response.status.code", context.Response.StatusCode);
            activity.SetTag("response.headers", JsonSerializer.Serialize(context.Response.Headers.ToDictionary(h => h.Key, h => h.Value.ToString())));
            activity.SetTag("Response.IsCompleted", context.Response.HasStarted ? "Yes" : "No");

            BlocksContext.ClearContext();
        }

        private static bool IsValidOriginOrReferer(HttpContext context, Tenant tenant)
        {
            var originHeader = context.Request.Headers["Origin"].FirstOrDefault();
            var refererHeader = context.Request.Headers["Referer"].FirstOrDefault();

            return IsDomainAllowed(originHeader, tenant) || IsDomainAllowed(refererHeader, tenant);
        }

        private static bool IsDomainAllowed(string? headerValue, Tenant tenant)
        {
            if (string.IsNullOrWhiteSpace(headerValue)) return true;

            try
            {
                var uri = new Uri(headerValue);
                var host = uri.Host;

                var normalizedApplicationDomain = NormalizeDomain(tenant.ApplicationDomain);
                var allowedDomains = tenant.AllowedDomains?.Select(NormalizeDomain) ?? Enumerable.Empty<string>();

                return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                       host.Equals(normalizedApplicationDomain, StringComparison.OrdinalIgnoreCase) ||
                       allowedDomains.Contains(host, StringComparer.OrdinalIgnoreCase);
            }
            catch (UriFormatException)
            {
                return false; // Invalid header format
            }
        }

        private static string NormalizeDomain(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain)) return string.Empty;

            return domain.Replace("http://", "")
                 .Replace("https://", "")
                 .Split(":")[0]
                 .Trim();
        }

        private static Task RejectRequest(HttpContext context, int statusCode, string message)
        {
            context.Response.StatusCode = statusCode;
            return context.Response.WriteAsync(JsonSerializer.Serialize(new BaseResponse
            {
                IsSuccess = false,
                Errors = new Dictionary<string, string> { { "Message", message } }
            }));
        }

        private static void AttachTenantDataToActivity(Tenant tenant)
        {
            var securityData = BlocksContext.Create(
                tenant.TenantId,
                Array.Empty<string>(),
                string.Empty,
                false,
                tenant.ApplicationDomain,
                string.Empty,
                DateTime.MinValue,
                string.Empty,
                Array.Empty<string>(),
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty
            );

            BlocksContext.SetContext(securityData, false);

            Baggage.SetBaggage("TenantId", tenant.TenantId);
            Baggage.SetBaggage("IsFromCloud", tenant.IsRootTenant.ToString());

            var current = Activity.Current;
            if (current != null)
            {
                current.SetTag("SecurityContext", JsonSerializer.Serialize(securityData));
                current.SetTag("ApplicationDomain", tenant.ApplicationDomain);
            }
        }


    }
}
