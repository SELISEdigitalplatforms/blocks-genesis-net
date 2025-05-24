using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
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
            context.Request.Headers.TryGetValue(BlocksConstants.BlocksKey, out var apiKey);
            bool apiKeyFoundInHeader = !StringValues.IsNullOrEmpty(apiKey);

            if (!apiKeyFoundInHeader)
            {
                context.Request.Query.TryGetValue(BlocksConstants.BlocksKey, out apiKey);
            }

            Tenant? tenant = null;
            if (StringValues.IsNullOrEmpty(apiKey))
            {
                var baseUrl = $"{context.Request.Scheme}://{context.Request.Host.Value}";

                tenant = _tenants.GetTenantByApplicationDomain(apiKey.ToString());

                if (tenant == null)
                {
                    await RejectRequest(context, StatusCodes.Status404NotFound, "Not_Found: Application_Not_Found");
                    return;
                }
            }

            if (tenant == null)
            {
                tenant = _tenants.GetTenantByID(apiKey.ToString());
            }

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
            if (Activity.Current == null) return;

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

            Activity.Current.SetCustomProperty("SecurityContext", JsonSerializer.Serialize(securityData));
        }

    }
}
