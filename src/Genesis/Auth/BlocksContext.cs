using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blocks.Genesis
{
    public sealed record BlocksContext
    {
        // JWT Standard Claims
        public const string ISSUER_CLAIM = "iss";
        public const string AUDIANCES_CLAIM = "aud";
        public const string ISSUED_AT_TIME_CLAIM = "iat";
        public const string NOT_BEFORE_THAT_CLAIM = "nbf";
        public const string EXPIRE_ON_CLAIM = "exp";
        public const string SUBJECT_CLAIM = "sub";

        // Custom Claims
        public const string TENANT_ID_CLAIM = "tenant_id";
        public const string ROLES_CLAIM = "roles";
        public const string USER_ID_CLAIM = "user_id";
        public const string REQUEST_URI_CLAIM = "request_uri";
        public const string TOKEN_CLAIM = "oauth";
        public const string PERMISSION_CLAIM = "permissions";
        public const string ORGANIZATION_ID_CLAIM = "org_id";
        public const string EMAIL_CLAIM = "email";
        public const string USER_NAME_CLAIM = "user_name";
        public const string DISPLAY_NAME_CLAIM = "name";
        public const string PHONE_NUMBER_CLAIM = "phone";

        private static readonly AsyncLocal<BlocksContext?> _asyncLocalContext = new();
        private static readonly ThreadLocal<bool> _isTestMode = new(() => false);
        private static readonly AsyncLocal<bool> _forceAsyncLocalContext = new();


        // Properties
        public string TenantId { get; private init; } = string.Empty;
        public IEnumerable<string> Roles { get; private init; } = [];
        public string UserId { get; private init; } = string.Empty;
        public DateTime ExpireOn { get; private init; } = DateTime.MinValue;
        public string RequestUri { get; private init; } = string.Empty;
        public string OAuthToken { get; private init; } = string.Empty;
        public string OrganizationId { get; private init; } = string.Empty;
        public bool IsAuthenticated { get; private init; }
        public string Email { get; private init; } = string.Empty;
        public IEnumerable<string> Permissions { get; private init; } = [];
        public string UserName { get; private init; } = string.Empty;
        public string PhoneNumber { get; private init; } = string.Empty;
        public string DisplayName { get; private init; } = string.Empty;
        public string ActualTenantId { get; private init; } = string.Empty;

        // Thread-safe test mode property
        public static bool IsTestMode
        {
            get => _isTestMode.Value;
            set => _isTestMode.Value = value;
        }

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
            string userName,
            string phoneNumber,
            string displayName,
            string oauthToken,
            string actualTenantId)
        {
            TenantId = tenantId ?? string.Empty;
            Roles = roles ?? Array.Empty<string>();
            UserId = userId ?? string.Empty;
            IsAuthenticated = isAuthenticated;
            RequestUri = requestUri ?? string.Empty;
            OrganizationId = organizationId ?? string.Empty;
            ExpireOn = expireOn;
            Email = email ?? string.Empty;
            Permissions = permissions ?? Array.Empty<string>();
            UserName = userName ?? string.Empty;
            PhoneNumber = phoneNumber ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            OAuthToken = oauthToken ?? string.Empty;
            ActualTenantId = actualTenantId ?? string.Empty;
        }


        /// <summary>
        /// Creates BlocksContext from ClaimsIdentity
        /// </summary>
        public static BlocksContext CreateFromClaimsIdentity(ClaimsIdentity claimsIdentity)
        {
            ArgumentNullException.ThrowIfNull(claimsIdentity);
            var thirdPartyContextHeader = GetHttpContext().Request.Headers[BlocksConstants.ThirdPartyContextHeader];

            if (!string.IsNullOrWhiteSpace(thirdPartyContextHeader))
            {
                return GetThitdPartyClainIdentity(thirdPartyContextHeader);
            }

            return new BlocksContext(
                tenantId: claimsIdentity.FindFirst(TENANT_ID_CLAIM)?.Value,
                roles: claimsIdentity?.FindAll(claimsIdentity.RoleClaimType).Select(r => r.Value).ToArray() ?? Enumerable.Empty<string>(),
                userId: claimsIdentity.FindFirst(USER_ID_CLAIM)?.Value,
                isAuthenticated: true,
                requestUri: claimsIdentity.FindFirst(REQUEST_URI_CLAIM)?.Value,
                organizationId: claimsIdentity.FindFirst(ORGANIZATION_ID_CLAIM)?.Value,
                expireOn: DateTime.TryParse(claimsIdentity.FindFirst(EXPIRE_ON_CLAIM)?.Value, out var exp) ? exp : DateTime.MinValue,
                email: claimsIdentity.FindFirst(EMAIL_CLAIM)?.Value,
                permissions: claimsIdentity.FindAll(PERMISSION_CLAIM).Select(c => c.Value).ToArray(),
                userName: claimsIdentity.FindFirst(USER_NAME_CLAIM)?.Value,
                phoneNumber: claimsIdentity.FindFirst(PHONE_NUMBER_CLAIM)?.Value,
                displayName: claimsIdentity.FindFirst(DISPLAY_NAME_CLAIM)?.Value,
                oauthToken: claimsIdentity.FindFirst(TOKEN_CLAIM)?.Value,
                actualTenantId: claimsIdentity.FindFirst(TENANT_ID_CLAIM)?.Value ?? string.Empty
            );
        }

        private static BlocksContext GetThitdPartyClainIdentity(string thirdPartyContextHeader)
        {
            var thirdPartyContext = JsonSerializer.Deserialize<BlocksContext>(thirdPartyContextHeader);

            return new BlocksContext(
                   tenantId: thirdPartyContext?.TenantId ?? "",
                   roles: thirdPartyContext?.Roles ?? [],
                   userId: thirdPartyContext?.UserId ?? string.Empty,
                   isAuthenticated: thirdPartyContext?.IsAuthenticated ?? false,
                   requestUri: thirdPartyContext?.RequestUri ?? string.Empty,
                   organizationId: thirdPartyContext?.OrganizationId ?? string.Empty,
                   expireOn: thirdPartyContext?.ExpireOn ?? DateTime.UtcNow.AddHours(1),
                   email: thirdPartyContext?.Email ?? string.Empty,
                   permissions: thirdPartyContext?.Permissions ?? [],
                   userName: thirdPartyContext?.UserName ?? string.Empty,
                   phoneNumber: thirdPartyContext?.PhoneNumber ?? string.Empty,
                   displayName: thirdPartyContext?.DisplayName ?? string.Empty,
                   oauthToken: thirdPartyContext?.OAuthToken ?? string.Empty,
                   actualTenantId: thirdPartyContext?.ActualTenantId ?? string.Empty);
        }

        /// <summary>
        /// Creates BlocksContext from individual parameters
        /// </summary>
        public static BlocksContext Create(
            string? tenantId,
            IEnumerable<string>? roles,
            string? userId,
            bool isAuthenticated,
            string? requestUri,
            string? organizationId,
            DateTime expireOn,
            string? email,
            IEnumerable<string>? permissions,
            string? userName,
            string? phoneNumber,
            string? displayName,
            string? oauthToken,
            string? actualTentId)
        {
            return new BlocksContext(tenantId, roles, userId, isAuthenticated, requestUri,
                organizationId, expireOn, email, permissions, userName, phoneNumber, displayName, oauthToken, actualTentId);
        }

        /// <summary>
        /// Gets the current BlocksContext from HTTP context or AsyncLocal storage
        /// Priority: HTTP Context (for API) > AsyncLocal (for background services/workers)
        /// </summary>
        public static BlocksContext? GetContext(BlocksContext? testValue = null)
        {
            try
            {
                // For testing scenarios
                if (IsTestMode)
                    return testValue ?? _asyncLocalContext.Value;

                if (_forceAsyncLocalContext.Value && _asyncLocalContext.Value != null)
                    return _asyncLocalContext.Value;

                var httpContext = GetHttpContext();
                if (httpContext?.User?.Identity is ClaimsIdentity identity && identity.IsAuthenticated)
                {
                    return CreateFromClaimsIdentity(identity);
                }

                return _asyncLocalContext.Value;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Sets the context in AsyncLocal storage (for background services/workers)
        /// </summary>
        public static void SetContext(BlocksContext? context, bool changeContext = true)
        {
            _asyncLocalContext.Value = context;
            _forceAsyncLocalContext.Value = context != null && changeContext;
        }

        /// <summary>
        /// Clears the current AsyncLocal context
        /// </summary>
        public static void ClearContext()
        {
            _asyncLocalContext.Value = null;
        }

        /// <summary>
        /// Executes an action within a specific BlocksContext
        /// </summary>
        public static void ExecuteInContext(BlocksContext context, Action action)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(action);

            var previousContext = _asyncLocalContext.Value;
            try
            {
                _asyncLocalContext.Value = context;
                action();
            }
            finally
            {
                _asyncLocalContext.Value = previousContext;
            }
        }

        /// <summary>
        /// Executes a function within a specific BlocksContext
        /// </summary>
        public static T ExecuteInContext<T>(BlocksContext context, Func<T> func)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(func);

            var previousContext = _asyncLocalContext.Value;
            try
            {
                _asyncLocalContext.Value = context;
                return func();
            }
            finally
            {
                _asyncLocalContext.Value = previousContext;
            }
        }

        private static HttpContext? GetHttpContext()
        {
            try
            {
                return BlocksHttpContextAccessor.Instance?.HttpContext;
            }
            catch
            {
                return null;
            }
        }

        public static void Cleanup()
        {
            _isTestMode?.Dispose();
        }

    }
}