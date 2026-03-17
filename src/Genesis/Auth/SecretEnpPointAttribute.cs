using Microsoft.AspNetCore.Authorization;

namespace Blocks.Genesis
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public class SecretEnpPointAttribute : AuthorizeAttribute
    {
        public SecretEnpPointAttribute()  : 
            base("Secret")
        { 
        }
    }
}
