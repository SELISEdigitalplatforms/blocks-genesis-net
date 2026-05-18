using Microsoft.AspNetCore.Http;
using OpenTelemetry;

namespace Blocks.Genesis;

internal sealed class RootTenantMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ITenants _tenants;
    private readonly ICacheClient _cacheClient;

    public RootTenantMiddleware(RequestDelegate next, ITenants tenants, ICacheClient cacheClient)
    {
        _next = next;
        _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
        _cacheClient = cacheClient ?? throw new ArgumentNullException(nameof(cacheClient));
    }
    public async Task InvokeAsync(HttpContext context)
    {
        var bc = BlocksContext.GetContext();
        var isRoot = _tenants.GetTenantByID(bc!.TenantId)?.IsRootTenant ?? false;

        if (isRoot)
        {
            var contextId = context.Request.Headers[BlocksConstants.ProjectContextIdHeader].FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(contextId))
            {
                var projectId = await _cacheClient.GetStringValueAsync(contextId).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(projectId))
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
                else
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsync("invalid_context_id").ConfigureAwait(false);
                    return;
                }
            }
        }

        await _next(context).ConfigureAwait(false);
    }
}
