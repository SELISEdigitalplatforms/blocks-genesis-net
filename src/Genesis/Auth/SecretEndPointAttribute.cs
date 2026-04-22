using Microsoft.AspNetCore.Authorization;

namespace Blocks.Genesis;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public class SecretEndPointAttribute : AuthorizeAttribute
{
    public SecretEndPointAttribute() : base("Secret")
    {
    }
}
