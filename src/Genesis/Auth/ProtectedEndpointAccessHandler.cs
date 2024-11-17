using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
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
            if (context.User.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
            {
                context.Fail();
                return;
            }

            var actionName = GetActionName(context);

            var controllerName = GetControllerName(context);

            var hasAccess = await CheckHasAccess(identity, actionName, controllerName);

            if (hasAccess)
            {
                context.Succeed(requirement);
            }
        }

        private async Task<bool> CheckHasAccess(ClaimsIdentity claimsIdentity, string actionName, string controllerName)
        {
            var resource = $"{controllerName}/{actionName}".ToLower();
            var roles = claimsIdentity.Claims.Where(c => c.Type == BlocksContext.ROLES_CLAIM).Select(c => c.Value);
            var permissions = claimsIdentity.Claims.Where(c => c.Type == BlocksContext.PERMISSION_CLAIM).Select(c => c.Value);

            return await CheckPermission(resource, roles, permissions);
        }

        private async Task<bool> CheckPermission(string resourse, IEnumerable<string> roles, IEnumerable<string> permission)
        {
            var collection = _dbContextProvider.GetCollection<BsonDocument>("Permissions");
            var filter = Builders<BsonDocument>.Filter.Eq("Type", 1)
                & Builders<BsonDocument>.Filter.Eq("Resource", resourse)
                & (Builders<BsonDocument>.Filter.In("Roles", roles) | Builders<BsonDocument>.Filter.In("Name", permission));
            return await collection.CountDocumentsAsync(filter) > 0;
        }

        public static string GetActionName(AuthorizationHandlerContext authorizationHandlerContext)
        {
            var filterContext = (authorizationHandlerContext.Resource as AuthorizationFilterContext);

            return (filterContext.ActionDescriptor as ControllerActionDescriptor).ActionName;
        }

        public static string GetControllerName(AuthorizationHandlerContext authorizationHandlerContext)
        {
            var filterContext = (authorizationHandlerContext.Resource as AuthorizationFilterContext);

            return (filterContext.ActionDescriptor as ControllerActionDescriptor).ControllerName;
        }
    }
}
