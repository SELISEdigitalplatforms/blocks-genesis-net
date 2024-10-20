using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;

namespace Blocks.Genesis
{
    public sealed record BlocksContext
    {
        private const string TENANT_ID_CLAIM = "tenantId";
        private const string ROLES_CLAIM = "roles";
        private const string USER_ID_CLAIM = "userId";
        private const string AUDIANCES_CLAIM = "audiances";
        private const string ORGANIZATION_ID_CLAIM = "organizationId";
        private const string IS_AUTHENTICATED_CLAIM = "isAuthenticated";
        private const string REQUEST_URI_CLAIM = "requestUri";

        // Properties with private setters
        public string TenantId { get; private init; }
        public IEnumerable<string> Roles { get; private init; }
        public string UserId { get; private init; }
        public IEnumerable<string> Audiances { get; private init; }
        public string RequestUri { get; private init; }
        public string OrganizationId { get; private init; }
        public bool IsAuthenticated { get; private init; }


        private BlocksContext(
            string tenantId,
            IEnumerable<string> roles,
            string userId,
            bool isAuthenticated,
            string requestUri,
            string organizationId)
        {
            TenantId = tenantId;
            Roles = roles;
            UserId = userId;
            IsAuthenticated = isAuthenticated;
            RequestUri = requestUri;
            OrganizationId = organizationId;
        }

        // Static method to create an instance from ClaimsIdentity
        internal static BlocksContext CreateFromClaimsIdentity(ClaimsIdentity claimsIdentity)
        {
            var tenantId = claimsIdentity.FindFirst(TENANT_ID_CLAIM)?.Value ?? string.Empty;
            var roles = claimsIdentity.FindAll(ROLES_CLAIM).Select(c => c.Value);
            var userId = claimsIdentity.FindFirst(USER_ID_CLAIM)?.Value ?? string.Empty;
            var audiances = claimsIdentity.FindAll(AUDIANCES_CLAIM).Select(c => c.Value);
            var requestUri = claimsIdentity.FindFirst(REQUEST_URI_CLAIM)?.Value ?? string.Empty;
            var organizationId = claimsIdentity.FindFirst(ORGANIZATION_ID_CLAIM)?.Value ?? string.Empty;
            var isAuthenticated = bool.TryParse(claimsIdentity.FindFirst(IS_AUTHENTICATED_CLAIM)?.Value, out var result) && result;

            return new BlocksContext(tenantId, roles, userId, isAuthenticated, requestUri, organizationId);
        }

        internal static BlocksContext CreateFromTuple((string tenantId, IEnumerable<string> roles, string userId, bool isAuthenticated, string requestUri, string organizationId) tuple)
        {
            return new BlocksContext(tuple.tenantId, tuple.roles, tuple.userId, tuple.isAuthenticated, tuple.requestUri, tuple.organizationId);
        }

        // Static method to retrieve the context from Activity
        public static BlocksContext? GetContext(string? value = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    var activity = Activity.Current;
                    if (activity != null)
                    {
                        var contextJson = activity.GetCustomProperty("SecurityContext")?.ToString();
                        return string.IsNullOrWhiteSpace(contextJson) ? null : JsonSerializer.Deserialize<BlocksContext>(contextJson);
                    }
                }

                return JsonSerializer.Deserialize<BlocksContext>(value ?? "");
            }
            catch (Exception ex)
            {

                Console.WriteLine(ex); ;
            }

            return null;
        }
    }
}
