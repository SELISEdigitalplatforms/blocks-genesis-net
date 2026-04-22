using Microsoft.AspNetCore.Authorization;

namespace Blocks.Genesis;

[Obsolete("Use SecretEndPointAttribute instead. This shim will be removed in the next major version.")]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public class SecretEnpPointAttribute : SecretEndPointAttribute
{
}
