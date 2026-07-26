using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Text;
using System.Text.Json;

namespace Blocks.Genesis
{
    public class RootTenantUrlValidationMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ITenants _tenants;
        private readonly IDbContextProvider _dbContextProvider;
        private readonly ILogger<RootTenantUrlValidationMiddleware> _logger;
        private const string AuthenticationConfigurationsCollectionName = "AuthenticationConfigurations";
        private List<string> _allowedApis = [];

        public RootTenantUrlValidationMiddleware(
            RequestDelegate next,
            ITenants tenants,
            IDbContextProvider dbContextProvider,
            ILogger<RootTenantUrlValidationMiddleware> logger,
            IBlocksSecret blocksSecret)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
            _dbContextProvider = dbContextProvider ?? throw new ArgumentNullException(nameof(dbContextProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        private void InitializeAllowedUrls(string tenantId)
        {
            if (_allowedApis.Count > 0) return;

            _allowedApis.Add("/");
            var document = _dbContextProvider
                .GetCollection<BsonDocument>(tenantId, AuthenticationConfigurationsCollectionName)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .FirstOrDefault();

            if (document == null)
            {
                _logger.LogWarning("Authentication configuration not found for Root tenant");
                return;
            }

            if (!document.TryGetValue("AllowedApis", out var allowedApisValue) ||
                allowedApisValue.BsonType != BsonType.Array)
            {
                _logger.LogWarning("AllowedApis not found or invalid for root tenant");
                return;
            }

            _allowedApis.AddRange(allowedApisValue
                .AsBsonArray
                .Where(x => x.IsString)
                .Select(x => x.AsString.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase));

            _logger.LogInformation(
                "Loaded {Count} allowed API path(s) for root tenant",
                _allowedApis.Count);

        }

        private async Task<bool> IsAllowed(HttpContext context, Tenant tenant)
        {
            var path = context.Request.Path.Value ?? "/";
            var isProtected = context.GetEndpoint()?.Metadata.GetMetadata<ProtectedEndPointAttribute>() != null;
            var projectKey = await ExtractProjectKeyAsync(context) ?? tenant.TenantId;

            if (isProtected &&
            tenant.IsRootTenant &&
            tenant.TenantId.Equals(projectKey, StringComparison.OrdinalIgnoreCase))
            {
                InitializeAllowedUrls(tenant.TenantId);
                if (!_allowedApis.Any(item => path.Equals(item, StringComparison.OrdinalIgnoreCase) || path.StartsWith(item + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }
            return true;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            context.Request.Headers.TryGetValue(BlocksConstants.BlocksKey, out var headerTenantId);
            var tenantId = headerTenantId.ToString();
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                await RejectRequest(
                    context,
                    StatusCodes.Status404NotFound,
                    "Not_Found: Application_Not_Found");

                return;
            }

            var tenant = _tenants.GetTenantByID(tenantId);

            if (tenant is null || tenant.IsDisabled)
            {
                await RejectRequest(
                    context,
                    StatusCodes.Status404NotFound,
                    "Not_Found: Application_Not_Found");

                return;
            }

            var isAllowed = await IsAllowed(context, tenant);
            if (!isAllowed)
            {
                await RejectRequest(context, StatusCodes.Status403Forbidden, "Access is not allowed.");
                return;
            }

            await _next(context);
        }

        private static Task RejectRequest(HttpContext context, int statusCode, string message)
        {
            context.Response.StatusCode = statusCode;

            return context.Response.WriteAsync(
                JsonSerializer.Serialize(new BaseResponse
                {
                    IsSuccess = false,
                    Errors = new Dictionary<string, string>
                    {
                        { "Message", message }
                    }
                }));
        }

        private static async Task<string?> ExtractProjectKeyAsync(HttpContext httpContext)
        {
            var request = httpContext.Request;

            var projectKey = request.Query
                .FirstOrDefault(q =>
                    string.Equals(q.Key, "ProjectKey", StringComparison.OrdinalIgnoreCase))
                .Value
                .ToString();

            if (!string.IsNullOrWhiteSpace(projectKey))
                return projectKey;

            if (request.ContentLength <= 0)
                return null;

            if (!request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) ?? true)
                return null;

            request.EnableBuffering();

            using var reader = new StreamReader(
                request.Body,
                Encoding.UTF8,
                leaveOpen: true);

            var body = await reader.ReadToEndAsync();

            request.Body.Position = 0;

            if (string.IsNullOrWhiteSpace(body))
                return null;

            try
            {
                using var json = JsonDocument.Parse(body);

                foreach (var property in json.RootElement.EnumerateObject())
                {
                    if (property.Name.Equals("projectKey", StringComparison.OrdinalIgnoreCase))
                    {
                        var value = property.Value.GetString();

                        if (!string.IsNullOrWhiteSpace(value))
                            return value;
                    }
                }
            }
            catch (JsonException)
            {
                // Ignore invalid JSON.
            }

            return null;
        }
    }
}