using Blocks.Genesis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Moq;
using System.Reflection;
using System.Security.Claims;

namespace XUnitTest.Auth;

/// <summary>
/// Branch coverage for the internal <c>SecretAuthorizationHandler</c>: non-HTTP resources,
/// missing tenant context and unknown tenants.
/// </summary>
[Collection("BlocksAuthStaticState")]
public class SecretEndPointHandlerCoverageTests
{
    private const string HandlerTypeName = "Blocks.Genesis.SecretAuthorizationHandler, Blocks.Genesis";
    private const string RequirementTypeName = "Blocks.Genesis.SecretEndPointRequirement, Blocks.Genesis";

    [Fact]
    public async Task HandleRequirementAsync_ShouldDoNothing_WhenResourceIsNotHttpContext()
    {
        var crypto = new Mock<ICryptoService>();
        var tenants = new Mock<ITenants>();

        var context = await InvokeAsync(crypto.Object, tenants.Object, resource: "not-http");

        Assert.False(context.HasFailed);
        Assert.False(context.HasSucceeded);
        crypto.Verify(c => c.ComputeHmacSha256(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task HandleRequirementAsync_ShouldFail_WhenTenantContextAndTenantAreMissing()
    {
        var originalTestMode = BlocksContext.IsTestMode;
        try
        {
            BlocksContext.IsTestMode = true;
            BlocksContext.ClearContext();

            var crypto = new Mock<ICryptoService>();
            crypto.Setup(c => c.ComputeHmacSha256(string.Empty, string.Empty, false)).Returns("computed");

            var tenants = new Mock<ITenants>();
            tenants.Setup(t => t.GetTenantByID(string.Empty)).Returns((Blocks.Genesis.Tenant?)null);

            var http = new DefaultHttpContext();
            http.Request.Headers["Secret"] = "some-secret";

            var context = await InvokeAsync(crypto.Object, tenants.Object, http);

            Assert.True(context.HasFailed);
            crypto.Verify(c => c.ComputeHmacSha256(string.Empty, string.Empty, false), Times.Once);
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
        }
    }

    // ---- helpers ----

    private static async Task<AuthorizationHandlerContext> InvokeAsync(ICryptoService crypto, ITenants tenants, object resource)
    {
        var type = Type.GetType(HandlerTypeName);
        var reqType = Type.GetType(RequirementTypeName);
        Assert.NotNull(type);
        Assert.NotNull(reqType);

        var handler = Activator.CreateInstance(type!, crypto, tenants);
        var requirement = (IAuthorizationRequirement)Activator.CreateInstance(reqType!)!;
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u")], "Bearer"));
        var context = new AuthorizationHandlerContext([requirement], principal, resource);

        var method = type!.GetMethod("HandleRequirementAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task)method!.Invoke(handler, [context, requirement])!;
        await task;

        return context;
    }
}
