using Microsoft.AspNetCore.Authorization;

namespace Blocks.Genesis
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public class ProtectedEndPointAttribute : AuthorizeAttribute
    {
        public ProtectedEndPointAttribute(string resourceName)
        {
            Policy = "Protected";
            ResourceName = resourceName;
        }

        public string ResourceName { get; }
    }
}
