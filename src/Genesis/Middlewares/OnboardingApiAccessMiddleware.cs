using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Blocks.Genesis;

public class OnboardingApiAccessMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ITenants _tenants;
    private readonly ILogger<OnboardingApiAccessMiddleware> _logger;
    private readonly IBlocksSecret _blocksSecret;
    private HashSet<string> _osAllowedApis = [];

    public OnboardingApiAccessMiddleware(
        RequestDelegate next,
        ITenants tenants,
        IBlocksSecret blocksSecret,
        ILogger<OnboardingApiAccessMiddleware> logger,
        string[] pathPrefixes)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
        _blocksSecret = blocksSecret;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        BlocksHttpContextAccessor.EnsureInitialized(context);

        var endpoint = context.GetEndpoint();
        if (endpoint is null || (endpoint.DisplayName?.Contains("Controller") == false && endpoint.DisplayName?.Contains("GraphQL") == false))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var blocksContext = BlocksContext.GetContext();
        if (blocksContext is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var tenantId = blocksContext.TenantId;
        var originalTenantId = blocksContext.OriginalTenantId;

        if (string.Equals(originalTenantId, tenantId, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        _osAllowedApis = _osAllowedApis.Count == 0? LoadOsAllowedApisFromRootDatabase(_blocksSecret): _osAllowedApis;

        var isOsAllowedApi = IsOsAllowedApi(context.Request.Path);
        var isRootTenant = IsRootTenant(tenantId);

        if (!isOsAllowedApi && !isRootTenant)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        await TenantContextHelper.RejectRequest(context, StatusCodes.Status403Forbidden, "Forbidden: Cross_Tenant_Access_Not_Allowed").ConfigureAwait(false);
    }

    private bool IsRootTenant(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return false;
        }

        var tenant = _tenants.GetTenantByID(tenantId);
        return tenant is { IsRootTenant: true };
    }

    private bool IsOsAllowedApi(PathString requestPath)
    {
        var normalized = NormalizePath(requestPath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return _osAllowedApis.Contains(requestPath);
    }

    private static string NormalizePath(PathString requestPath)
    {
        var value = requestPath.Value?.Trim('/') ?? string.Empty;
        return value;
    }

    private HashSet<string> LoadOsAllowedApisFromRootDatabase(IBlocksSecret blocksSecret)
    {
       
       var database = new MongoClient(blocksSecret.DatabaseConnectionString)
           .GetDatabase(blocksSecret.RootDatabaseName);

       var document = database
           .GetCollection<BsonDocument>("AuthenticationConfigurations")
           .Find(FilterDefinition<BsonDocument>.Empty)
           .FirstOrDefault();

       if (document?.TryGetValue("AllowedApis", out var allowedApis) == true &&
                allowedApis.IsBsonArray)
       {
           return allowedApis.AsBsonArray
               .Select(x => x.AsString)
               .ToHashSet(StringComparer.OrdinalIgnoreCase);
       }
       

      return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }
}
