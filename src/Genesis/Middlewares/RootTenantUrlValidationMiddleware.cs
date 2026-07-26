using Microsoft.AspNetCore.Http;
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
        private readonly ICryptoService _cryptoService;
        private List<string> _allowedApis = [];
        private readonly IDbContextProvider _dbContextProvider;
        private const string authenticationConfigurationscollectionName = "AuthenticationConfigurations";

        public RootTenantUrlValidationMiddleware(RequestDelegate next, ITenants tenants, ICryptoService cryptoService, IDbContextProvider dbContextProvider)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
            _cryptoService = cryptoService ?? throw new ArgumentNullException(nameof(cryptoService));
            _dbContextProvider = dbContextProvider;
        }

        private void initAllowedUrl(string tenantId)
        {
            var document = _dbContextProvider.GetCollection<BsonDocument>(tenantId, authenticationConfigurationscollectionName).Find(FilterDefinition<BsonDocument>.Empty)
         .FirstOrDefault();
            if (document == null)
                return;

            _allowedApis = document["AllowedApis"]
                .AsBsonArray
                .Select(x => x.AsString)
                .ToList();

        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                context.Request.Headers.TryGetValue(BlocksConstants.BlocksKey, out var headerTenantId);
                var tenantId = headerTenantId.ToString();
                if (string.IsNullOrWhiteSpace(tenantId))
                {
                    await RejectRequest(context, StatusCodes.Status404NotFound, "Not_Found: Application_Not_Found");
                    return;
                }

                Tenant? tenant = _tenants.GetTenantByID(tenantId);

                if (tenant is null || tenant.IsDisabled)
                {
                    await RejectRequest(context, StatusCodes.Status404NotFound, "Not_Found: Application_Not_Found");
                    return;
                }
                var projectKey = await ExtractProjectKeyAsync(context);
                var path = context.Request.Path;
                if (tenant.IsRootTenant && (tenant.TenantId == projectKey || String.IsNullOrWhiteSpace(projectKey)) )
                {
                    if (_allowedApis.Count == 0)
                    {
                        initAllowedUrl(tenant.TenantId);
                    }
                    var isValid = _allowedApis.Any(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
                    if (!isValid)
                    {
                        await RejectRequest(context, StatusCodes.Status403Forbidden, "Access is not allowed.");
                        return;

                    }
                }
                await _next(context);
            }
            finally
            {
            }
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

        private async Task<string?> ExtractProjectKeyAsync(HttpContext httpContext)
        {
            var request = httpContext.Request;

            var projectKeyFromQuery = request.Query.FirstOrDefault(q => string.Equals(q.Key, "ProjectKey", StringComparison.OrdinalIgnoreCase)).Value.ToString();

            if (!string.IsNullOrWhiteSpace(projectKeyFromQuery))
            {
                return projectKeyFromQuery;
            }

            if (request.ContentLength > 0 && request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
            {

                request.EnableBuffering();
                using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
                var body = await reader.ReadToEndAsync();
                request.Body.Position = 0;

                if (!string.IsNullOrWhiteSpace(body))
                {
                    try
                    {
                        using var jsonDoc = JsonDocument.Parse(body);
                        var projectKeyProperty = jsonDoc.RootElement.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, "projectKey", StringComparison.OrdinalIgnoreCase));

                        if (projectKeyProperty.Value.ValueKind != JsonValueKind.Undefined)
                        {
                            var projectKeyFromBody = projectKeyProperty.Value.GetString();
                            if (!string.IsNullOrWhiteSpace(projectKeyFromBody))
                                return projectKeyFromBody;
                        }
                    }
                    catch (JsonException)
                    {
                        // Body is not valid JSON; ignore
                    }
                }
            }

            return null;
        }


    }
}
