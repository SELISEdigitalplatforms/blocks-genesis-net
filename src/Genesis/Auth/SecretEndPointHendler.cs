using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Blocks.Genesis
{
    internal class SecretAuthorizationHandler : AuthorizationHandler<SecretEndPointRequirement>
    {
        private readonly ICryptoService _cryptoService;
        private readonly ITenants _tenants;

        public SecretAuthorizationHandler(ICryptoService cryptoService, ITenants tenants)
        {
            _cryptoService = cryptoService;
            _tenants = tenants;
        }

        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SecretEndPointRequirement requirement)
        {
            if (context.Resource is HttpContext httpContext)
            {
                var secret = httpContext.Request.Headers["Secret"].ToString();
                var tenantId = BlocksContext.GetContext()?.TenantId;
                var salt = _tenants.GetTenantByID(tenantId)?.TenantSalt;
                var actulalSecret = _cryptoService.Hash(tenantId, salt);

                if (string.IsNullOrEmpty(secret) || secret != actulalSecret)
                {
                    context.Fail();
                }
                else
                {
                    context.Succeed(requirement);
                }
            }

            return Task.CompletedTask;
        }
    }
}
