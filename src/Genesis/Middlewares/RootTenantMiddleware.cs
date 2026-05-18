using Microsoft.AspNetCore.Http;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenTelemetry;

namespace Blocks.Genesis;

internal sealed class RootTenantMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ITenants _tenants;
    private readonly ICacheClient _cacheClient;
    private readonly IDbContextProvider _dbContextProvider;

    public RootTenantMiddleware(RequestDelegate next, ITenants tenants, ICacheClient cacheClient, IDbContextProvider dbContextProvider)
    {
        _next = next;
        _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
        _cacheClient = cacheClient ?? throw new ArgumentNullException(nameof(cacheClient));
        _dbContextProvider = dbContextProvider ?? throw new ArgumentNullException(nameof(dbContextProvider));
    }
    public async Task InvokeAsync(HttpContext context)
    {
        var bc = BlocksContext.GetContext();
        var isRoot = _tenants.GetTenantByID(bc!.TenantId)?.IsRootTenant ?? false;

        if (isRoot)
        {
            var contextId = context.Request.Headers[BlocksConstants.ProjectContextIdHeader].FirstOrDefault();

            if(string.IsNullOrWhiteSpace(contextId))
            {
                var isExist = await _dbContextProvider.GetCollection<BsonDocument>("ProjectPeoples")
                    .Find(Builders<BsonDocument>.Filter.Eq("UserId", bc?.UserId)
                    & Builders<BsonDocument>.Filter.Eq("TenantId", bc?.TenantId))
                    .Limit(1).AnyAsync().ConfigureAwait(false);

                if (!isExist)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/json";
                    var errorResponse = new { status = "forbidden", message = "access_denied" };
                    await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(errorResponse)).ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                var projectId = await _cacheClient.GetStringValueAsync(contextId).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(projectId))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json";
                    var errorResponse = new { status = "unauthorized", message = "invalid_context_id" };
                    await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(errorResponse)).ConfigureAwait(false);
                    return;
                }
                else
                {
                    Baggage.SetBaggage("ActualTenantId", bc?.TenantId ?? string.Empty);

                    BlocksContext.SetContext(BlocksContext.Create
                     (
                        tenantId: projectId,
                        roles: bc?.Roles ?? Enumerable.Empty<string>(),
                        userId: bc?.UserId ?? string.Empty,
                        isAuthenticated: bc?.IsAuthenticated ?? false,
                        requestUri: bc?.RequestUri ?? string.Empty,
                        organizationId: bc?.OrganizationId ?? string.Empty,
                        expireOn: bc?.ExpireOn ?? DateTime.UtcNow.AddHours(1),
                        email: bc?.Email ?? string.Empty,
                        permissions: bc?.Permissions ?? Enumerable.Empty<string>(),
                        userName: bc?.UserName ?? string.Empty,
                        phoneNumber: bc?.PhoneNumber ?? string.Empty,
                        displayName: bc?.DisplayName ?? string.Empty,
                        oauthToken: bc?.OAuthToken ?? string.Empty,
                        actualTenantId: bc?.TenantId ?? string.Empty,
                        applicationDomain: bc?.ApplicationDomain ?? string.Empty
                    ));

                    Baggage.SetBaggage("TenantId", projectId);
                }
                
            }

        }

        await _next(context).ConfigureAwait(false);
    }
}
