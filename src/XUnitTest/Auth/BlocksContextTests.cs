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
    }
}