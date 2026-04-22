using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;
using System.Text;

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
                var actualSecret = _cryptoService.ComputeHmacSha256(tenantId ?? string.Empty, salt ?? string.Empty);
                var legacySecret = _cryptoService.Hash(tenantId ?? string.Empty, salt ?? string.Empty);

                var isValid = !string.IsNullOrEmpty(secret)
                    && (ConstantTimeEquals(secret, actualSecret)
                        || ConstantTimeEquals(secret, legacySecret));

                if (!isValid)
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

        private static bool ConstantTimeEquals(string left, string right)
        {
            var leftBytes = Encoding.UTF8.GetBytes(left ?? string.Empty);
            var rightBytes = Encoding.UTF8.GetBytes(right ?? string.Empty);
            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
    }
}
