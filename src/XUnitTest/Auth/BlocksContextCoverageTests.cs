using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using Moq;
using System.Reflection;
using System.Security.Claims;

namespace XUnitTest.Auth;

/// <summary>
/// Branch coverage for <c>BlocksContext</c>: claims-only tenant resolution without an HTTP
/// context, the defensive catch in <c>GetContext</c>, the private constructor's null
/// coalescing, and the transport sanitization masks.
/// </summary>
[Collection("BlocksAuthStaticState")]
public class BlocksContextCoverageTests
{
    [Fact]
    public void CreateFromClaimsIdentity_ShouldReadOriginalTenantFromClaims_WhenNoHttpContext()
    {
        var originalAccessor = BlocksHttpContextAccessor.Instance;
        try
        {
            BlocksHttpContextAccessor.Instance = new HttpContextAccessor { HttpContext = null };

            var identity = new ClaimsIdentity(
            [
                new Claim(BlocksContext.TENANT_ID_CLAIM, "tenant-claims"),
                new Claim(BlocksContext.USER_ID_CLAIM, "user-7")
            ], "Bearer");

            var context = BlocksContext.CreateFromClaimsIdentity(identity);

            Assert.Equal("tenant-claims", context.OriginalTenantId);
            Assert.Equal("user-7", context.UserId);
        }
        finally
        {
            BlocksHttpContextAccessor.Instance = originalAccessor;
        }
    }

    [Fact]
    public void CreateFromClaimsIdentity_ShouldDefaultOriginalTenantToEmpty_WhenNoClaimAndNoHttpContext()
    {
        var originalAccessor = BlocksHttpContextAccessor.Instance;
        try
        {
            BlocksHttpContextAccessor.Instance = new HttpContextAccessor { HttpContext = null };

            var context = BlocksContext.CreateFromClaimsIdentity(new ClaimsIdentity());

            Assert.Equal(string.Empty, context.OriginalTenantId);
            Assert.Equal(string.Empty, context.TenantId);
        }
        finally
        {
            BlocksHttpContextAccessor.Instance = originalAccessor;
        }
    }

    [Fact]
    public void GetContext_ShouldReturnNull_WhenHttpContextUserAccessThrows()
    {
        var originalAccessor = BlocksHttpContextAccessor.Instance;
        var originalTestMode = BlocksContext.IsTestMode;
        try
        {
            BlocksContext.IsTestMode = false;
            BlocksContext.SetContext(null);

            var httpContext = new Mock<HttpContext>();
            httpContext.SetupGet(c => c.User).Throws(new InvalidOperationException("user unavailable"));

            var accessor = new Mock<IHttpContextAccessor>();
            accessor.SetupGet(a => a.HttpContext).Returns(httpContext.Object);
            BlocksHttpContextAccessor.Instance = accessor.Object;

            Assert.Null(BlocksContext.GetContext());
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
            BlocksHttpContextAccessor.Instance = originalAccessor;
        }
    }

    [Fact]
    public void GetContext_ShouldFallBackToAsyncLocal_WhenHttpUserIsNotAuthenticated()
    {
        var originalAccessor = BlocksHttpContextAccessor.Instance;
        var originalTestMode = BlocksContext.IsTestMode;
        try
        {
            BlocksContext.IsTestMode = false;

            var stored = BlocksContext.Create(
                "tenant-async", [], "user-async", false, "", "", DateTime.MinValue, "", [], "", "", "", "", "tenant-async");
            BlocksContext.SetContext(stored, changeContext: false);

            BlocksHttpContextAccessor.Instance = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };

            var resolved = BlocksContext.GetContext();

            Assert.Same(stored, resolved);
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = originalTestMode;
            BlocksHttpContextAccessor.Instance = originalAccessor;
        }
    }

    [Fact]
    public void PrivateConstructor_ShouldCoalesceNullArguments()
    {
        // Records also carry a compiler-generated copy constructor; pick the JSON one.
        var ctor = typeof(BlocksContext).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(c => c.GetParameters().Length == 17);

        var context = (BlocksContext)ctor.Invoke(
        [
            null, null, null, true, null, null, DateTime.MinValue, null, null, null, null, null, null, null, null, false, null
        ]);

        Assert.Equal(string.Empty, context.TenantId);
        Assert.Empty(context.Roles);
        Assert.Equal(string.Empty, context.UserId);
        Assert.Equal(string.Empty, context.RequestUri);
        Assert.Equal(string.Empty, context.OrganizationId);
        Assert.Equal(string.Empty, context.Email);
        Assert.Empty(context.Permissions);
        Assert.Equal(string.Empty, context.UserName);
        Assert.Equal(string.Empty, context.PhoneNumber);
        Assert.Equal(string.Empty, context.DisplayName);
        Assert.Equal(string.Empty, context.OAuthToken);
        Assert.Equal(string.Empty, context.OriginalTenantId);
        Assert.Equal(string.Empty, context.ApplicationDomain);
        Assert.Equal(string.Empty, context.ImpersonationSessionId);
    }

    [Fact]
    public void CreateSanitizedForTransport_ShouldReturnEmptyObject_ForNullContext()
    {
        var sanitized = BlocksContext.CreateSanitizedForTransport(null);

        Assert.NotNull(sanitized);
        Assert.Empty(sanitized.GetType().GetProperties());
    }

    [Theory]
    [InlineData("user@example.com", "***@example.com")]
    [InlineData("no-at-sign", "***")]
    [InlineData("", "***")]
    public void CreateSanitizedForTransport_ShouldMaskEmail(string email, string expected)
    {
        var context = BlocksContext.Create(
            "tenant-1", [], "user-1", true, "", "", DateTime.MinValue, email, [], "", "", "", "", "tenant-1");

        var sanitized = BlocksContext.CreateSanitizedForTransport(context);
        var masked = (string)sanitized.GetType().GetProperty("Email")!.GetValue(sanitized)!;

        Assert.Equal(expected, masked);
    }

    [Theory]
    [InlineData("15551234567", "***4567")]
    [InlineData("123", "***123")]
    [InlineData("***", "***")]
    [InlineData("", "***")]
    public void CreateSanitizedForTransport_ShouldMaskPhoneNumber(string phone, string expected)
    {
        var context = BlocksContext.Create(
            "tenant-1", [], "user-1", true, "", "", DateTime.MinValue, "", [], "", phone, "", "", "tenant-1");

        var sanitized = BlocksContext.CreateSanitizedForTransport(context);
        var masked = (string)sanitized.GetType().GetProperty("PhoneNumber")!.GetValue(sanitized)!;

        Assert.Equal(expected, masked);
    }
}
