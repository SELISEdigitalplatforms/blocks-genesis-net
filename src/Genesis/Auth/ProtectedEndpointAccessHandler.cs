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
            // Check if the user is authenticated
            if (context.User.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
            {
                context.Fail();
                return;
            }

            // Get action and controller names
            var actionName = GetActionName(context);
            var controllerName = GetControllerName(context);

            if (string.IsNullOrEmpty(actionName) || string.IsNullOrEmpty(controllerName))
            {
                context.Fail();
                return;
            }

            if (context.Resource is HttpContext httpContext)
            {
                var blocksKey = httpContext.Request.Headers[BlocksConstants.BlocksKey].ToString();
                var isRoot = _tenants.GetTenantByID(blocksKey)?.IsRootTenant ?? false;
                var projectKey = await ExtractProjectKeyAsync(httpContext);

                if (isRoot && !string.IsNullOrEmpty(projectKey))
                {
                    var userId = identity.FindFirst(BlocksContext.USER_ID_CLAIM)?.Value;
                    var tenant = _tenants.GetTenantByID(projectKey);

                    if (tenant != null && (tenant.CreatedBy == userId))
                    {
                        // Allow access
                        context.Succeed(requirement);
                        return;
                    }

                    var sharedProject = await (await _dbContextProvider.GetCollection<BsonDocument>("ProjectPeoples").FindAsync(Builders<BsonDocument>.Filter.Eq("UserId", userId) & Builders<BsonDocument>.Filter.Eq("TenantId", projectKey))).FirstOrDefaultAsync();

                    if (sharedProject != null)
                    {
                        context.Succeed(requirement);
                        return;
                    }

                    // Check access
                    var hasAccess = await CheckHasAccess(identity, actionName, controllerName);

                    if (hasAccess)
                    {
                        context.Succeed(requirement);
                    }
                    else
                    {
                        context.Fail();
                    }
                }
            }
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

            var filter =  Builders<BsonDocument>.Filter.In("Resource", permissions) |
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

        private static async Task<string?> ExtractProjectKeyAsync(HttpContext httpContext)
        {
            var request = httpContext.Request;

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
