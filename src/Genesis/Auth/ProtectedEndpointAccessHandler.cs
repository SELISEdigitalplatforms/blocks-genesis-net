using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
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

        private async Task<bool> CheckHasAccess(ClaimsIdentity claimsIdentity, string actionName, string controllerName)
        {
            var resource = $"{controllerName}::{actionName}".ToLower();
            var roles = claimsIdentity.FindAll(BlocksContext.ROLES_CLAIM).Select(c => c.Value);
            var permissions = claimsIdentity.FindAll(BlocksContext.PERMISSION_CLAIM).Select(c => c.Value);

            return await CheckPermission(resource, roles, permissions);
        }

        private async Task<bool> CheckPermission(string resource, IEnumerable<string> roles, IEnumerable<string> permissions)
        {
            var collection = _dbContextProvider.GetCollection<BsonDocument>("Permissions");

            var filter = Builders<BsonDocument>.Filter.Eq("Type", 1)
                         & Builders<BsonDocument>.Filter.Eq("Resource", resource)
                         & (Builders<BsonDocument>.Filter.In("Roles", roles)
                            | Builders<BsonDocument>.Filter.In("Name", permissions));

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

    }
}
