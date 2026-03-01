using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using System.Text.Json;

namespace XUnitTest.Auth;

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
    public void Create_ShouldConvertNullInputsToSafeDefaults()
    {
        var context = BlocksContext.Create(null, null, null, false, null, null, DateTime.MinValue, null, null, null, null, null, null, null);

        Assert.Equal(string.Empty, context.TenantId);
        Assert.Equal(string.Empty, context.UserId);
        Assert.Empty(context.Roles);
        Assert.Empty(context.Permissions);
        Assert.False(context.IsAuthenticated);
    }

    [Fact]
    public void SetContext_ThenGetContext_ShouldReturnSameContextInTestMode()
    {
        var expected = CreateContext("tenant-a", "user-a");

        BlocksContext.SetContext(expected);
        var actual = BlocksContext.GetContext();

        Assert.Equal("tenant-a", actual?.TenantId);
        Assert.Equal("user-a", actual?.UserId);
    }

    [Fact]
    public void ExecuteInContext_Action_ShouldRestorePreviousContext()
    {
        var previous = CreateContext("tenant-prev", "user-prev");
        var scoped = CreateContext("tenant-scoped", "user-scoped");
        BlocksContext.SetContext(previous);

        BlocksContext.ExecuteInContext(scoped, () =>
        {
            Assert.Equal("tenant-scoped", BlocksContext.GetContext()?.TenantId);
        });

        Assert.Equal("tenant-prev", BlocksContext.GetContext()?.TenantId);
    }

    [Fact]
    public void ExecuteInContext_Func_ShouldReturnValueAndRestorePreviousContext()
    {
        var previous = CreateContext("tenant-prev", "user-prev");
        var scoped = CreateContext("tenant-scoped", "user-scoped");
        BlocksContext.SetContext(previous);

        var value = BlocksContext.ExecuteInContext(scoped, () => BlocksContext.GetContext()?.UserId);

        Assert.Equal("user-scoped", value);
        Assert.Equal("tenant-prev", BlocksContext.GetContext()?.TenantId);
    }

    [Fact]
    public void CreateFromClaimsIdentity_ShouldReadClaims()
    {
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
    }

    [Fact]
    public void CreateFromClaimsIdentity_ShouldUseThirdPartyHeader_WhenAvailable()
    {
        var thirdParty = CreateContext("tenant-third", "user-third");
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["ThirdPartyContext"] = JsonSerializer.Serialize(thirdParty);
        BlocksHttpContextAccessor.Instance = new HttpContextAccessor { HttpContext = httpContext };

        var identity = new ClaimsIdentity([new Claim(BlocksContext.TENANT_ID_CLAIM, "tenant-main")], "Bearer");
        var result = BlocksContext.CreateFromClaimsIdentity(identity);

        Assert.Equal("tenant-third", result.TenantId);
        Assert.Equal("user-third", result.UserId);
    }

    private static BlocksContext CreateContext(string tenantId, string userId)
    {
        return BlocksContext.Create(
            tenantId,
            ["role"],
            userId,
            true,
            "/api/test",
            "org-1",
            DateTime.UtcNow.AddHours(1),
            "user@example.com",
            ["perm"],
            "username",
            "123",
            "display",
            "token",
            tenantId);
    }
}