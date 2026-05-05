using Microsoft.AspNetCore.Authorization;

namespace Blocks.Genesis
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public class ProtectedEndPointAttribute : AuthorizeAttribute
    {
        public string? ResourceName { get; set; }

        public ProtectedEndPointAttribute(string? resourceName = null) :
            base("Protected")
        {
            ResourceName = resourceName;
        }
    }
}
