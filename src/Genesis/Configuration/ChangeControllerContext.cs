using System.Diagnostics;

namespace Blocks.Genesis
{
    public class ChangeControllerContext
    {
        private readonly ITenants _tenants;

        public ChangeControllerContext(ITenants tenants)
        {
            _tenants = tenants;
        }

        public void ChangeContext(IProjectKey projectKey)
        {
            var bc = BlocksContext.GetContext();

            Activity.Current?.SetCustomProperty("ActualTenantId", bc.TenantId);

            if (string.IsNullOrWhiteSpace(projectKey.ProjectKey) || projectKey.ProjectKey == bc?.TenantId) return;
            var isRoot = _tenants.GetTenantByID(bc?.TenantId)?.IsRootTenant ?? false;

            if (isRoot)
            {
                BlocksContext.SetContext(BlocksContext.Create
                 (
                    tenantId: projectKey.ProjectKey,
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
                    oauthToken: bc?.OAuthToken ?? string.Empty
                ));

                Activity.Current?.SetCustomProperty("TenantId", projectKey.ProjectKey);
            }
        }
    }
}
