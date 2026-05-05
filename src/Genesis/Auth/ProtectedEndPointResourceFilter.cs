using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Blocks.Genesis
{
    /// <summary>
    /// ActionFilter that extracts ResourceName from ProtectedEndPointAttribute
    /// and stores it in HttpContext.Items for the authorization handler to use.
    /// </summary>
    public class ProtectedEndPointResourceFilter : IAsyncActionFilter
    {
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var descriptor = context.ActionDescriptor as ControllerActionDescriptor;

            if (descriptor != null)
            {
                var protectedEndPointAttribute = descriptor.MethodInfo
                    .GetCustomAttributes(typeof(ProtectedEndPointAttribute), false)
                    .FirstOrDefault() as ProtectedEndPointAttribute;

                if (protectedEndPointAttribute?.ResourceName != null)
                {
                    context.HttpContext.Items[BlocksConstants.ProtectedResourceName] = protectedEndPointAttribute.ResourceName;
                }
            }

            await next();
        }
    }
}
