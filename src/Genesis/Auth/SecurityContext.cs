using System.Security.Claims;

namespace Blocks.Genesis
{
    internal record SecurityContext : ISecurityContext
    {
        private const string TENANT_ID_CLAIM = "tenantId";
        private const string ROLES_CLAIM = "roles";
        private const string OAUTH_BEARER_TOKEN_CLAIM = "oauthBearerToken";
        private const string USER_ID_CLAIM = "userId";
        private const string AUDIANCES_CLAIM = "audiances";
        private const string ORGANIZATION_ID_CLAIM = "organizationId";
        private const string IS_AUTHENTICATED_CLAIM = "isAuthenticated";
        private const string REQUEST_URI_CLAIM = "requestUri";

        public IEnumerable<string> Roles { get; }
        public string TenantId { get; }
        public string OauthBearerToken { get; }
        public string UserId { get; }
        public IEnumerable<string> Audiances { get; }
        public Uri RequestUri { get; }
        public string OrganizationId { get; }
        public bool IsAuthenticated { get; }

        private SecurityContext((string TenantId, IEnumerable<string> Roles, string OauthBearerToken, string UserId, bool IsAuthenticated, Uri RequestUri, string OrganizationId) tuple)
        {
            TenantId = tuple.TenantId;
            Roles = tuple.Roles;
            OauthBearerToken = tuple.OauthBearerToken;
            UserId = tuple.UserId;
            IsAuthenticated = tuple.IsAuthenticated;
            RequestUri = tuple.RequestUri;
            OrganizationId = tuple.OrganizationId;
        }

        private SecurityContext(ClaimsIdentity claimsIdentity)
        {
            TenantId = claimsIdentity.FindFirst(TENANT_ID_CLAIM)?.Value ?? string.Empty;
            Roles = claimsIdentity.FindAll(ROLES_CLAIM).Select(c => c.Value);
            OauthBearerToken = claimsIdentity.FindFirst(OAUTH_BEARER_TOKEN_CLAIM)?.Value ?? string.Empty;
            UserId = claimsIdentity.FindFirst(USER_ID_CLAIM)?.Value ?? string.Empty;
            Audiances = claimsIdentity.FindAll(AUDIANCES_CLAIM).Select(c => c.Value);
            RequestUri = new Uri(claimsIdentity.FindFirst(REQUEST_URI_CLAIM)?.Value ?? string.Empty);
            OrganizationId = claimsIdentity.FindFirst(ORGANIZATION_ID_CLAIM)?.Value ?? string.Empty;
            IsAuthenticated = bool.TryParse(claimsIdentity.FindFirst(IS_AUTHENTICATED_CLAIM)?.Value, out var isAuthenticated) && isAuthenticated;
        }

        public static SecurityContext CreateFromTuple((string TenantId, IEnumerable<string> Roles, string OauthBearerToken, string UserId, bool IsAuthenticated, Uri RequestUri, string OrganizationId) tuple)
        {
            return new SecurityContext(tuple);
        }

        public static SecurityContext CreateFromClaimsIdentity(ClaimsIdentity claimsIdentity)
        {
            return new SecurityContext(claimsIdentity);
        }



    }
}
