using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blocks.Genesis
{
    public sealed record BlocksContext
    {
        public const string TENANT_ID_CLAIM = "t_id";
        public const string ROLES_CLAIM = "roles";
        public const string USER_ID_CLAIM = "u_id";
        public const string AUDIANCES_CLAIM = "aud";
        public const string IS_AUTHENTICATED_CLAIM = "isAuthenticated";
        public const string REQUEST_URI_CLAIM = "ruri";
        public const string PERMISSION_CLAIM = "permissions";
        public const string ISSUED_AT_TIME_CLAIM = "iat";
        public const string ORGANIZATION_ID_CLAIM = "o_id";
        public const string NOT_BEFORE_THAT_CLAIM = "nbf";
        public const string EXPIRE_ON_CLAIM = "exp";
        public const string EMAIL_CLAIM = "u_email";
        public const string USER_NAME_CLAIM = "u_name";
        public const string ISSUER_CLAIM = "iss";

        // Properties with private setters
        public string TenantId { get; private init; }
        public IEnumerable<string> Roles { get; private init; }
        public string UserId { get; private init; }
        public DateTime ExpireOn { get; private init; }
        public string RequestUri { get; private init; }
        public string OrganizationId { get; private init; }
        public bool IsAuthenticated { get; private init; }
        public string Email { get; private init; }
        public IEnumerable<string> Permissions { get; private init; }
        public string UserName { get; private init; }

        [JsonConstructor]
        private BlocksContext(
            string tenantId,
            IEnumerable<string> roles,
            string userId,
            bool isAuthenticated,
            string requestUri,
            string organizationId,
            DateTime expireOn,
            string email,
            IEnumerable<string> permissions,
            string userName)
        {
            TenantId = tenantId;
            Roles = roles;
            UserId = userId;
            IsAuthenticated = isAuthenticated;
            RequestUri = requestUri;
            OrganizationId = organizationId;
            ExpireOn = expireOn;
            Email = email;
            Permissions = permissions;
            UserName = userName;
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
            var isAuthenticated = bool.TryParse(claimsIdentity.FindFirst(IS_AUTHENTICATED_CLAIM)?.Value, out var isAuth) && isAuth;
            var expireOn = DateTime.TryParse(claimsIdentity.FindFirst(EXPIRE_ON_CLAIM)?.Value, out var exp) ? exp : DateTime.MinValue;
            var email = claimsIdentity.FindFirst(EMAIL_CLAIM)?.Value ?? string.Empty;
            var permissions = claimsIdentity.FindAll(PERMISSION_CLAIM).Select(c => c.Value);
            var userName = claimsIdentity.FindFirst(USER_NAME_CLAIM)?.Value ?? string.Empty;

            return new BlocksContext(tenantId, roles, userId, isAuthenticated, requestUri, organizationId, expireOn, email, permissions, userName);
        }

        internal static BlocksContext CreateFromTuple((string tenantId, IEnumerable<string> roles, string userId, bool isAuthenticated, string requestUri, string organizationId, DateTime expireOn, string email, IEnumerable<string> permissions, string userName) tuple)
        {
            return new BlocksContext(tuple.tenantId, tuple.roles, tuple.userId, tuple.isAuthenticated, tuple.requestUri, tuple.organizationId, tuple.expireOn, tuple.email, tuple.permissions, tuple.userName);
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

                    return null;
                }

                return JsonSerializer.Deserialize<BlocksContext>(value);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error deserializing BlocksContext: {ex.Message}");
                return null;
            }
        }
    }
}
