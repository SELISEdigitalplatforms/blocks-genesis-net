using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
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
            var httpContext = GetHttpContext(context.Resource);
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

        private static HttpContext? GetHttpContext(object? resource)
        {
            return resource switch
            {
                HttpContext context => context,
                AuthorizationFilterContext mvcContext => mvcContext.HttpContext,
                _ => null
            };
        }

        private static string? GetExplicitResourceName(HttpContext? httpContext)
        {
            // Legacy path: a custom filter/middleware may copy it into HttpContext.Items.
            if (httpContext?.Items.TryGetValue(BlocksConstants.ProtectedResourceName, out var resourceName) == true)
            {
                return resourceName?.ToString();
            }

            // Primary path: resolve from endpoint metadata during authorization.
            var endpointResourceName = httpContext?
                .GetEndpoint()?
                .Metadata
                .GetMetadata<ProtectedEndPointAttribute>()?
                .ResourceName;

            return string.IsNullOrWhiteSpace(endpointResourceName) ? null : endpointResourceName;
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
            var bc = BlocksContext.GetContext();
            string? tenantId = null;
            if (bc != null)
            {
                tenantId = bc.Impersonated ? bc.ActualTenantId ?? bc.TenantId : bc.TenantId;
            }
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                return false;
            }

            var collection = _dbContextProvider.GetCollection<BsonDocument>(tenantId, "Permissions");
            var organizationId = bc?.OrganizationId;
            if (string.IsNullOrWhiteSpace(organizationId))
            {
                organizationId = "default";
            }

            // Single query: check if a document exists for this org where either:
            // - Resource is in permissions (direct permission)
            // - Resource matches and Roles contains any of the user's roles (role-based permission)
            var filter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("OrganizationId", organizationId),
                Builders<BsonDocument>.Filter.Or(
                    Builders<BsonDocument>.Filter.In("Resource", permissions),
                    Builders<BsonDocument>.Filter.And(
                        Builders<BsonDocument>.Filter.Eq("Resource", resource),
                        Builders<BsonDocument>.Filter.In("Roles", roles)
                    )
                )
            );

            return await collection.CountDocumentsAsync(filter).ConfigureAwait(false) > 0;
        }


    }
}
