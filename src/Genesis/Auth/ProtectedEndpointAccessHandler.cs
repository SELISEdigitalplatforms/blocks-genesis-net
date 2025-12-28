using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Security.Claims;
using System.Text.Json;
using System.Text;

namespace Blocks.Genesis
{
    internal class ProtectedEndpointAccessHandler : AuthorizationHandler<ProtectedEndpointAccessRequirement>
    {
        private readonly IDbContextProvider _dbContextProvider;
        private readonly IBlocksSecret _blocksSecret;
        private readonly ITenants _tenants;

        public ProtectedEndpointAccessHandler(IDbContextProvider dbContextProvider,
                                              IBlocksSecret blocksSecret,
                                              ITenants tenants)
        {
            _dbContextProvider = dbContextProvider;
            _blocksSecret = blocksSecret;
            _tenants = tenants;
        }

        protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ProtectedEndpointAccessRequirement requirement)
        {
            if (!IsAuthenticated(context))
            {
                context.Fail();
                return;
            }

            var identity = (ClaimsIdentity)context.User.Identity!;
            var (actionName, controllerName) = GetActionAndController(context);

            if (string.IsNullOrEmpty(actionName) || string.IsNullOrEmpty(controllerName))
            {
                context.Fail();
                return;
            }

            if (!(await IsWithinQuotaAsync(context, identity, actionName, controllerName, requirement)) && context.Resource is HttpContext httpRequest)
            {
                httpRequest.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await httpRequest.Response.WriteAsJsonAsync(new { error = "Too many requests" });

                context.Fail(new AuthorizationFailureReason(this, "RATE_LIMIT_EXCEEDED"));
                return;
            }

            if (context.Resource is HttpContext httpContext)
            {
                if (await HandleRootTenantAccessAsync(context, identity, httpContext, requirement))
                    return;
            }

            await HandleStandardAccessAsync(context, identity, actionName, controllerName, requirement);
        }

        private async Task<bool> IsWithinQuotaAsync(AuthorizationHandlerContext context,
                                                     ClaimsIdentity identity,
                                                     string actionName,
                                                     string controllerName,
                                                     ProtectedEndpointAccessRequirement requirement)
        {
            var projectKey =  await ExtractProjectKeyAsync((HttpContext)context.Resource);
            var tenantId = !string.IsNullOrWhiteSpace(projectKey) ? projectKey : BlocksContext.GetContext()?.TenantId;
            var resource = $"{_blocksSecret.ServiceName}::{controllerName}::{actionName}".ToLower();
            var database = _dbContextProvider.GetDatabase(tenantId!);
            var resourceLimitCollection = database.GetCollection<BsonDocument>("ResourceLimits");

            var filter = Builders<BsonDocument>.Filter.Eq("Resource", resource);

            var resourceLimit = await (await resourceLimitCollection.FindAsync(filter)).FirstOrDefaultAsync();

            if (resourceLimit is not null && (resourceLimit["Limit"].ToInt64() - resourceLimit["Usage"].ToInt64()) <= 0)
            {
                return false;
            }

            return true;

        }

        private static bool IsAuthenticated(AuthorizationHandlerContext context)
        {
            return context.User.Identity is ClaimsIdentity identity && identity.IsAuthenticated;
        }

        private static (string Action, string Controller) GetActionAndController(AuthorizationHandlerContext context)
        {
            var action = GetActionName(context);
            var controller = GetControllerName(context);
            return (action, controller);
        }

        private async Task<bool> HandleRootTenantAccessAsync(AuthorizationHandlerContext context,
                                                             ClaimsIdentity identity,
                                                             HttpContext httpContext,
                                                             ProtectedEndpointAccessRequirement requirement)
        {
            var projectKey = await ExtractProjectKeyAsync(httpContext);
            if (string.IsNullOrEmpty(projectKey))
                return false;

            var userId = identity.FindFirst(BlocksContext.USER_ID_CLAIM)?.Value;
            if (string.IsNullOrEmpty(userId))
                return false;

            if (await IsProjectOwnerOrSharedAsync(userId, projectKey))
            {
                context.Succeed(requirement);
                return true;
            }

            return false;
        }

        private async Task<bool> IsProjectOwnerOrSharedAsync(string userId, string projectKey)
        {
            var tenant = _tenants.GetTenantByID(projectKey);
            if (tenant != null && tenant.CreatedBy == userId)
                return true;

            var filter = Builders<BsonDocument>.Filter.Eq("UserId", userId) &
                         Builders<BsonDocument>.Filter.Eq("TenantId", projectKey);

            var sharedProject = await (await _dbContextProvider
                .GetCollection<BsonDocument>("ProjectPeoples")
                .FindAsync(filter))
                .FirstOrDefaultAsync();

            return sharedProject != null;
        }

        private async Task HandleStandardAccessAsync(AuthorizationHandlerContext context,
                                                     ClaimsIdentity identity,
                                                     string actionName,
                                                     string controllerName,
                                                     ProtectedEndpointAccessRequirement requirement)
        {
            var hasAccess = await CheckHasAccess(identity, actionName, controllerName);

            if (hasAccess)
                context.Succeed(requirement);
            else
                context.Fail();
        }



        private async Task<bool> CheckHasAccess(ClaimsIdentity claimsIdentity, string actionName, string controllerName)
        {
            var resource = $"{_blocksSecret.ServiceName}::{controllerName}::{actionName}".ToLower();
            var roles = claimsIdentity?.FindAll(claimsIdentity.RoleClaimType).Select(r => r.Value).ToArray() ?? Enumerable.Empty<string>();
            var permissions = claimsIdentity.FindAll(BlocksContext.PERMISSION_CLAIM).Select(c => c.Value);

            return await CheckPermission(resource, roles, permissions);
        }

        private async Task<bool> CheckPermission(string resource, IEnumerable<string> roles, IEnumerable<string> permissions)
        {
            var collection = _dbContextProvider.GetCollection<BsonDocument>("Permissions");

            var filter = Builders<BsonDocument>.Filter.In("Resource", permissions) |
                          ((Builders<BsonDocument>.Filter.In("Roles", roles) &
                          Builders<BsonDocument>.Filter.Eq("Resource", resource)));

            return await collection.CountDocumentsAsync(filter) > 0;
        }

        private static string GetActionName(AuthorizationHandlerContext authorizationHandlerContext)
        {
            if (authorizationHandlerContext.Resource is HttpContext httpContext)
            {
                var endpoint = httpContext.GetEndpoint();
                var controllerActionDescriptor = endpoint?.Metadata.GetMetadata<ControllerActionDescriptor>();
                return controllerActionDescriptor?.ActionName ?? string.Empty;
            }

            return string.Empty;
        }

        private static string GetControllerName(AuthorizationHandlerContext authorizationHandlerContext)
        {
            if (authorizationHandlerContext.Resource is HttpContext httpContext)
            {
                var endpoint = httpContext.GetEndpoint();
                var controllerActionDescriptor = endpoint?.Metadata.GetMetadata<ControllerActionDescriptor>();
                return controllerActionDescriptor?.ControllerName ?? string.Empty;
            }

            return string.Empty;
        }

        private async Task<string?> ExtractProjectKeyAsync(HttpContext httpContext)
        {
            var request = httpContext.Request;

            var blocksKey = httpContext.Request.Headers[BlocksConstants.BlocksKey].ToString();
            var tenant = _tenants.GetTenantByID(blocksKey);

            if (tenant is null || !tenant.IsRootTenant)
                return null;

            if (request.Query.TryGetValue("ProjectKey", out var queryValue))
            {
                var projectKeyFromQuery = queryValue.ToString();
                if (!string.IsNullOrWhiteSpace(projectKeyFromQuery))
                    return projectKeyFromQuery;
            }

            if (request.ContentLength > 0 && request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
            {
                // Allow multiple reads of the body (required!)
                request.EnableBuffering();

                using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
                var body = await reader.ReadToEndAsync();
                request.Body.Position = 0; // rewind so the next middleware can read again

                if (!string.IsNullOrWhiteSpace(body))
                {
                    try
                    {
                        using var jsonDoc = JsonDocument.Parse(body);
                        if (jsonDoc.RootElement.TryGetProperty("projectKey", out var projectKeyElement))
                        {
                            var projectKeyFromBody = projectKeyElement.GetString();
                            if (!string.IsNullOrWhiteSpace(projectKeyFromBody))
                                return projectKeyFromBody;
                        }
                    }
                    catch (JsonException)
                    {
                        // Body is not valid JSON; ignore
                    }
                }
            }

            return null;
        }


    }
}
