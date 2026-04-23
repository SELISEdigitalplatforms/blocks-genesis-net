using Blocks.Genesis;
using Microsoft.AspNetCore.Http;

namespace XUnitTest.Middlewares;

public class SecurityHeadersMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ShouldSetAllSecurityHeaders_AndCallNext()
    {
        var nextInvoked = false;
        var middleware = new SecurityHeadersMiddleware(_ =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        await middleware.InvokeAsync(context);

        Assert.True(nextInvoked);
        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", context.Response.Headers["X-Frame-Options"]);
        Assert.Equal("strict-origin-when-cross-origin", context.Response.Headers["Referrer-Policy"]);
        Assert.Equal("default-src 'self'; frame-ancestors 'none'", context.Response.Headers["Content-Security-Policy"]);
        Assert.Equal("camera=(), microphone=(), geolocation=()", context.Response.Headers["Permissions-Policy"]);
    }

    [Fact]
    public async Task InvokeAsync_ShouldPropagateExceptionsFromNext()
    {
        var middleware = new SecurityHeadersMiddleware(_ => throw new InvalidOperationException("downstream"));
        var context = new DefaultHttpContext();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));
        Assert.Equal("downstream", ex.Message);

        // Headers are still set before calling next.
        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"]);
    }
}
