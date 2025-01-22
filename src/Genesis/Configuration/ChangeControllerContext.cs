using System.Diagnostics;
using System.Text.Json;

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
            if (string.IsNullOrWhiteSpace(projectKey.ProjectKey) || projectKey.ProjectKey == bc?.TenantId) return;

            var isRoot = _tenants.GetTenantByID(bc?.TenantId)?.IsRootTenant ?? false;

            if (isRoot)
            {
                Activity.Current?.SetCustomProperty("SecurityContext", JsonSerializer.Serialize(new
                {
                    TenantId = projectKey.ProjectKey,
                    Roles = bc?.Roles,
                    UserId = bc?.UserId,
                    ExpireOn = bc?.ExpireOn,
                    RequestUri = bc?.RequestUri,
                    OrganizationId = bc?.OrganizationId,
                    IsAuthenticated = bc?.IsAuthenticated,
                    Email = bc?.Email,
                    Permissions = bc?.Permissions,
                    UserName = bc?.UserName,
                }));
                Activity.Current?.SetCustomProperty("TenantId", projectKey.ProjectKey);
            }
        }
    }
}
