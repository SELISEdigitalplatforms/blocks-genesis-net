using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Security.Claims;

namespace Blocks.Genesis
{
    internal class ProtectedEndpointAccessHandler : AuthorizationHandler<ProtectedEndpointAccessRequirement>
    {
        private readonly IDbContextProvider _dbContextProvider;

        public ProtectedEndpointAccessHandler(IDbContextProvider dbContextProvider)
        {
            _dbContextProvider = dbContextProvider;
        }

        protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ProtectedEndpointAccessRequirement requirement)
        {
            if (!IsAuthenticated(context))
            {
                context.Fail();
                return;
            }

            var identity = (ClaimsIdentity)context.User.Identity!;
            var httpContext = context.Resource as HttpContext;
            var resourceName = GetExplicitResourceName(httpContext);
            
            // ResourceName is MANDATORY
            if (string.IsNullOrEmpty(resourceName))
            {
                context.Fail(new AuthorizationFailureReason(this, "PROTECTED_RESOURCE_REQUIRED"));
                return;
            }

            var tenantId = BlocksContext.GetContext()?.TenantId;
            if (!(await IsWithinQuotaAsync(identity, resourceName, tenantId)) && httpContext != null)
            {
                httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await httpContext.Response.WriteAsJsonAsync(new BaseResponse { IsSuccess = false, Errors = new Dictionary<string, string> { { "exceed_limit", "Request limit exceeded." } } });
                context.Fail(new AuthorizationFailureReason(this, "RATE_LIMIT_EXCEEDED"));
                return;
            }

            var hasAccess = await CheckHasAccess(identity, resourceName);
            if (hasAccess)
                context.Succeed(requirement);
            else
                context.Fail();
        }

        /// <summary>
        /// Check rate limit quota for the resource
        /// </summary>
        private async Task<bool> IsWithinQuotaAsync(ClaimsIdentity identity,
                                                     string resourceName,
                                                     string? tenantId)
        {
            if (string.IsNullOrEmpty(tenantId))
                return true; // Skip quota check if no tenant context

            var database = _dbContextProvider.GetDatabase(tenantId);
            var resourceLimitCollection = database.GetCollection<BsonDocument>("ResourceLimits");

            var filter = Builders<BsonDocument>.Filter.Eq("Resource", resourceName);
            var resourceLimit = await (await resourceLimitCollection.FindAsync(filter)).FirstOrDefaultAsync();

            if (resourceLimit is not null && (resourceLimit["Limit"].ToInt64() - resourceLimit["Usage"].ToInt64()) <= 0)
            {
                return false;
            }

            return true;
        }

        private static string? GetExplicitResourceName(HttpContext? httpContext)
        {
            if (httpContext?.Items.TryGetValue(BlocksConstants.ProtectedResourceName, out var resourceName) == true)
            {
                return resourceName?.ToString();
            }
            return null;
        }

        private static bool IsAuthenticated(AuthorizationHandlerContext context)
        {
            return context.User.Identity is ClaimsIdentity identity && identity.IsAuthenticated;
        }

        /// <summary>
        /// Check if user has permission to access the resource
        /// </summary>
        private async Task<bool> CheckHasAccess(ClaimsIdentity claimsIdentity, string resourceName)
        {
            var permissions = claimsIdentity.FindAll(BlocksContext.PERMISSION_CLAIM).Select(c => c.Value);
            return await CheckPermission(resourceName, BlocksContext.GetContext()?.Roles ?? [], permissions);
        }

        private async Task<bool> CheckPermission(string resource, IEnumerable<string> roles, IEnumerable<string> permissions)
        {
            var collection = _dbContextProvider.GetCollection<BsonDocument>("Permissions");
            var organizationId = BlocksContext.GetContext()?.OrganizationId;
            if (string.IsNullOrWhiteSpace(organizationId))
            {
                organizationId = "default";
            }

            var directPermissionFilter = Builders<BsonDocument>.Filter.In("Resource", permissions);
            var roleFilter = Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.In($"Roles.{organizationId}", roles),
                Builders<BsonDocument>.Filter.In("Roles", roles));

            var filter = directPermissionFilter |
                         (roleFilter & Builders<BsonDocument>.Filter.Eq("Resource", resource));

            return await collection.CountDocumentsAsync(filter) > 0;
        }


    }
}
