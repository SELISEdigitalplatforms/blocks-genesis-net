using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using System.Text.Json;

namespace XUnitTest.Auth;

[Collection("BlocksAuthStaticState")]
public class BlocksContextTests : IDisposable
{
    public BlocksContextTests()
    {
        BlocksContext.IsTestMode = true;
        BlocksContext.ClearContext();
        BlocksHttpContextAccessor.Instance = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
    }

    public void Dispose()
    {
        BlocksContext.ClearContext();
        BlocksContext.IsTestMode = false;
        BlocksHttpContextAccessor.Instance = null;
    }

    [Fact]
    public void CreateFromClaimsIdentity_ShouldReadClaims()
    {
        BlocksHttpContextAccessor.Instance = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };

        var identity = new ClaimsIdentity(
        [
            new Claim(BlocksContext.TENANT_ID_CLAIM, "tenant-1"),
            new Claim(BlocksContext.USER_ID_CLAIM, "user-1"),
            new Claim(ClaimTypes.Role, "admin"),
            new Claim(BlocksContext.PERMISSION_CLAIM, "read"),
            new Claim(BlocksContext.EMAIL_CLAIM, "u@example.com")
        ], "Bearer");

        var context = BlocksContext.CreateFromClaimsIdentity(identity);

        Assert.Equal("tenant-1", context.TenantId);
        Assert.Equal("user-1", context.UserId);
        Assert.Contains("admin", context.Roles);
        Assert.Contains("read", context.Permissions);
        Assert.Equal("u@example.com", context.Email);
        Assert.Equal(string.Empty, context.ClientId);
    }

    [Fact]
    public void CreateFromClaimsIdentity_ShouldReadClientId_ForClientCredentialToken()
    {
        BlocksHttpContextAccessor.Instance = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };

        var identity = new ClaimsIdentity(
        [
            new Claim(BlocksContext.TENANT_ID_CLAIM, "tenant-1"),
            new Claim(BlocksContext.CLIENT_ID_CLAIM, "client-1")
        ], "Bearer");

        var context = BlocksContext.CreateFromClaimsIdentity(identity);

        Assert.Equal("client-1", context.ClientId);
        Assert.Equal(string.Empty, context.UserId);
    }

    [Fact]
    public void CreateSanitizedForTransport_ShouldCarryClientId()
    {
        var context = BlocksContext.Create(
            tenantId: "tenant-1",
            roles: null,
            userId: null,
            isAuthenticated: true,
            requestUri: null,
            organizationId: null,
            expireOn: DateTime.MinValue,
            email: null,
            permissions: null,
            userName: null,
            phoneNumber: null,
            displayName: null,
            oauthToken: null,
            originalTenantId: "tenant-1",
            clientId: "client-1");

        var sanitized = BlocksContext.CreateSanitizedForTransport(context);
        var clientId = sanitized.GetType().GetProperty("ClientId")?.GetValue(sanitized);

        Assert.Equal("client-1", clientId);
    }
}