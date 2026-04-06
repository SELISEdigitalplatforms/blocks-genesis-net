using Blocks.Genesis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Moq;
using System.Reflection;
using System.Security.Claims;

namespace XUnitTest.Auth;

public class AuthAttributesAndSecretHandlerTests
{
    [Fact]
    public void ProtectedEndPointAttribute_ShouldUseProtectedPolicy()
    {
        var attribute = new ProtectedEndPointAttribute();
        Assert.Equal("Protected", attribute.Policy);
    }

    [Fact]
    public void SecretEnpPointAttribute_ShouldUseSecretPolicy()
    {
        var attribute = new SecretEnpPointAttribute();
        Assert.Equal("Secret", attribute.Policy);
    }

    [Fact]
    public void InternalRequirementTypes_ShouldExist()
    {
        var protectedReqType = Type.GetType("Blocks.Genesis.ProtectedEndpointAccessRequirement, Blocks.Genesis");
        var secretReqType = Type.GetType("Blocks.Genesis.SecretEndPointRequirement, Blocks.Genesis");

        Assert.NotNull(protectedReqType);
        Assert.NotNull(secretReqType);
        Assert.True(typeof(IAuthorizationRequirement).IsAssignableFrom(protectedReqType));
        Assert.True(typeof(IAuthorizationRequirement).IsAssignableFrom(secretReqType));
    }

    [Fact]
    public async Task SecretAuthorizationHandler_ShouldFail_WhenSecretHeaderIsMissing()
    {
        var originalTestMode = BlocksContext.IsTestMode;
        var originalAccessor = BlocksHttpContextAccessor.Instance;
        try
        {
            BlocksContext.IsTestMode = true;
            BlocksContext.SetContext(BlocksContext.Create("tenant-a", [], "", false, "", "", DateTime.MinValue, "", [], "", "", "", "", "", "tenant-a"));

            var crypto = new Mock<ICryptoService>();
            crypto.Setup(c => c.Hash("tenant-a", "salt-a")).Returns("expected-secret");

            var tenants = new Mock<ITenants>();
            tenants.Setup(t => t.GetTenantByID("tenant-a")).Returns(CreateTenant("tenant-a", "salt-a"));

            var type = Type.GetType("Blocks.Genesis.SecretAuthorizationHandler, Blocks.Genesis");
            var reqType = Type.GetType("Blocks.Genesis.SecretEndPointRequirement, Blocks.Genesis");
            Assert.NotNull(type);
            Assert.NotNull(reqType);

            var handler = Activator.CreateInstance(type!, crypto.Object, tenants.Object);
            var requirement = (IAuthorizationRequirement)Activator.CreateInstance(reqType!)!;
            var authContext = new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u")], "Bearer")), new DefaultHttpContext());

            var method = type!.GetMethod("HandleRequirementAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var task = (Task)method!.Invoke(handler, [authContext, requirement])!;
            await task;

            Assert.True(authContext.HasFailed);
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
            BlocksHttpContextAccessor.Instance = originalAccessor;
        }
    }

    [Fact]
    public async Task SecretAuthorizationHandler_ShouldSucceed_WhenSecretHeaderMatchesHashedValue()
    {
        var originalTestMode = BlocksContext.IsTestMode;
        var originalAccessor = BlocksHttpContextAccessor.Instance;
        try
        {
            BlocksContext.IsTestMode = true;
            BlocksContext.SetContext(BlocksContext.Create("tenant-b", [], "", false, "", "", DateTime.MinValue, "", [], "", "", "", "", "", "tenant-b"));

            var crypto = new Mock<ICryptoService>();
            crypto.Setup(c => c.Hash("tenant-b", "salt-b")).Returns("expected-secret");

            var tenants = new Mock<ITenants>();
            tenants.Setup(t => t.GetTenantByID("tenant-b")).Returns(CreateTenant("tenant-b", "salt-b"));

            var type = Type.GetType("Blocks.Genesis.SecretAuthorizationHandler, Blocks.Genesis");
            var reqType = Type.GetType("Blocks.Genesis.SecretEndPointRequirement, Blocks.Genesis");
            Assert.NotNull(type);
            Assert.NotNull(reqType);

            var handler = Activator.CreateInstance(type!, crypto.Object, tenants.Object);
            var requirement = (IAuthorizationRequirement)Activator.CreateInstance(reqType!)!;
            var http = new DefaultHttpContext();
            http.Request.Headers["Secret"] = "expected-secret";
            var authContext = new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u")], "Bearer")), http);

            var method = type!.GetMethod("HandleRequirementAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var task = (Task)method!.Invoke(handler, [authContext, requirement])!;
            await task;

            Assert.True(authContext.HasSucceeded);
            Assert.False(authContext.HasFailed);
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
            BlocksHttpContextAccessor.Instance = originalAccessor;
        }
    }

    private static Blocks.Genesis.Tenant CreateTenant(string tenantId, string salt)
    {
        return new Blocks.Genesis.Tenant
        {
            TenantId = tenantId,
            TenantSalt = salt,
            ApplicationDomain = "app.local",
            DbConnectionString = "mongodb://localhost:27017",
            JwtTokenParameters = new JwtTokenParameters
            {
                Issuer = "issuer",
                Subject = "subject",
                Audiences = [],
                PublicCertificatePath = "none",
                PublicCertificatePassword = string.Empty,
                PrivateCertificatePassword = string.Empty,
                IssueDate = DateTime.UtcNow
            }
        };
    }
}
