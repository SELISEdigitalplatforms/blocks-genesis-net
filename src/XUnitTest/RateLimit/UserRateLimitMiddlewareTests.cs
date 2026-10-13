using Blocks.Genesis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using System.Text.Json;

namespace XUnitTest.RateLimit;

public class UserRateLimitMiddlewareTests
{
    private sealed class FakeLimiter(Func<string, RateLimitDecision?> decide) : IUserRateLimiter
    {
        public List<string> Keys { get; } = [];

        public Task<RateLimitDecision?> TryAcquireAsync(string key, CancellationToken cancellationToken = default)
        {
            Keys.Add(key);
            return Task.FromResult(decide(key));
        }
    }

    private sealed class TrailersFeature : IHttpResponseTrailersFeature
    {
        public IHeaderDictionary Trailers { get; set; } = new HeaderDictionary();
    }

    private static DefaultHttpContext Context(
        FakeLimiter? limiter,
        ClaimsPrincipal user,
        params object[] metadata)
    {
        var services = new ServiceCollection();
        if (limiter is not null)
        {
            services.AddSingleton<IUserRateLimiter>(limiter);
            services.AddSingleton(new UserRateLimitSettings(20, UserRateLimitSettings.EnvironmentSource, "svc-a"));
        }

        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = user,
            TraceIdentifier = "trace-1"
        };
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "test"));
        return context;
    }

    private static async Task<bool> Run(DefaultHttpContext context)
    {
        var nextCalled = false;
        var middleware = new UserRateLimitMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(context);
        return nextCalled;
    }

    private static string Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    [Fact]
    public async Task Allowed_AddsRateLimitHeaders_AndRunsEndpoint() // H4
    {
        var limiter = new FakeLimiter(_ => new RateLimitDecision(true, 19, 1));
        var context = Context(limiter, RateLimitSubjectTests.Principal("u1", tenantId: "t1"), new AuthorizeAttribute());

        Assert.True(await Run(context));
        Assert.Equal("\"user\";q=20;w=1", context.Response.Headers[UserRateLimitMiddleware.RateLimitPolicyHeader]);
        Assert.Equal("\"user\";r=19;t=1", context.Response.Headers[UserRateLimitMiddleware.RateLimitHeader]);
        Assert.Equal("ratelimit:svc-a:t1:user:u1", Assert.Single(limiter.Keys));
    }

    [Fact]
    public async Task Rejected_WritesProblemDetails_WithoutRunningEndpoint() // H5, C15
    {
        var limiter = new FakeLimiter(_ => new RateLimitDecision(false, 0, 30));
        var context = Context(limiter, RateLimitSubjectTests.Principal(clientId: "c9", tenantId: "t1"), new ProtectedEndPointAttribute("res"));

        Assert.False(await Run(context));
        Assert.Equal(429, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        Assert.Equal("30", context.Response.Headers.RetryAfter);
        Assert.Equal("\"client\";q=20;w=1", context.Response.Headers[UserRateLimitMiddleware.RateLimitPolicyHeader]);
        Assert.Equal("\"client\";r=0;t=30", context.Response.Headers[UserRateLimitMiddleware.RateLimitHeader]);

        using var body = JsonDocument.Parse(Body(context));
        Assert.Equal("https://httpstatuses.com/429", body.RootElement.GetProperty("type").GetString());
        Assert.Equal("Rate Limit Exceeded", body.RootElement.GetProperty("title").GetString());
        Assert.Equal(429, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Rate limit exceeded. Retry after 30 seconds.", body.RootElement.GetProperty("detail").GetString());
        Assert.Equal("trace-1", body.RootElement.GetProperty("instance").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rejected_Grpc_EndsWithResourceExhausted(bool supportsTrailers) // H12
    {
        var limiter = new FakeLimiter(_ => new RateLimitDecision(false, 0, 30));
        var context = Context(limiter, RateLimitSubjectTests.Principal("u1", tenantId: "t1"), new AuthorizeAttribute());
        context.Request.ContentType = "application/grpc+proto";
        var trailers = new TrailersFeature();
        if (supportsTrailers)
        {
            context.Features.Set<IHttpResponseTrailersFeature>(trailers);
        }

        Assert.False(await Run(context));
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/grpc", context.Response.ContentType);

        var source = supportsTrailers ? trailers.Trailers : context.Response.Headers;
        Assert.Equal("8", source["grpc-status"]);
        Assert.Equal("Rate limit exceeded", source["grpc-message"]);
        Assert.Equal("30", source["retry-after"]);
        Assert.Equal(string.Empty, Body(context));
    }

    [Fact]
    public async Task PublicAndOpenEndpoints_AreNeverLimited() // C1
    {
        var limiter = new FakeLimiter(_ => new RateLimitDecision(false, 0, 30));
        var user = RateLimitSubjectTests.Principal("u1", tenantId: "t1");

        var anonymous = Context(limiter, user, new AuthorizeAttribute(), new AllowAnonymousAttribute());
        var open = Context(limiter, user);
        var noEndpoint = Context(limiter, user);
        noEndpoint.SetEndpoint(null);

        Assert.True(await Run(anonymous));
        Assert.True(await Run(open));
        Assert.True(await Run(noEndpoint));
        Assert.Empty(limiter.Keys);
        Assert.False(anonymous.Response.Headers.ContainsKey(UserRateLimitMiddleware.RateLimitHeader));
    }

    [Fact]
    public async Task UnauthenticatedOrSubjectless_IsNotLimited() // C2, C3
    {
        var limiter = new FakeLimiter(_ => new RateLimitDecision(false, 0, 30));

        Assert.True(await Run(Context(limiter, new ClaimsPrincipal(new ClaimsIdentity()), new AuthorizeAttribute())));
        Assert.True(await Run(Context(limiter, RateLimitSubjectTests.Principal(tenantId: "t1"), new AuthorizeAttribute())));
        Assert.Empty(limiter.Keys);
    }

    [Fact]
    public async Task FailOpen_WhenLimiterCannotDecide_OrIsNotRegistered() // C8
    {
        var undecided = new FakeLimiter(_ => null);
        var context = Context(undecided, RateLimitSubjectTests.Principal("u1"), new AuthorizeAttribute());
        var unregistered = Context(null, RateLimitSubjectTests.Principal("u1"), new AuthorizeAttribute());

        Assert.True(await Run(context));
        Assert.True(await Run(unregistered));
        Assert.False(context.Response.Headers.ContainsKey(UserRateLimitMiddleware.RateLimitHeader));
        Assert.False(context.Response.Headers.ContainsKey(UserRateLimitMiddleware.RateLimitPolicyHeader));
    }

    [Fact]
    public async Task Key_IgnoresIdentityHeaders() // C11
    {
        var limiter = new FakeLimiter(_ => new RateLimitDecision(true, 5, 1));
        var context = Context(limiter, RateLimitSubjectTests.Principal("u1", tenantId: "t1"), new AuthorizeAttribute());
        context.Request.Headers["X-User-Id"] = "u2";
        context.Request.Headers["X-Client-Id"] = "c2";
        context.Request.Headers["X-Forwarded-For"] = "10.0.0.9";

        await Run(context);

        Assert.Equal("ratelimit:svc-a:t1:user:u1", Assert.Single(limiter.Keys));
    }

    [Fact]
    public async Task Constructor_And_Invoke_ValidateArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new UserRateLimitMiddleware(null!));
        var middleware = new UserRateLimitMiddleware(_ => Task.CompletedTask);
        await Assert.ThrowsAsync<ArgumentNullException>(() => middleware.InvokeAsync(null!));
    }

    [Fact]
    public void Helpers_FormatHeaders_AndDetectGrpc()
    {
        Assert.Equal("\"user\";q=100;w=1", UserRateLimitMiddleware.FormatPolicy("user", 100, 1));
        Assert.Equal("\"client\";r=3;t=2", UserRateLimitMiddleware.FormatRateLimit("client", 3, 2));
        var context = new DefaultHttpContext();
        Assert.False(UserRateLimitMiddleware.IsGrpcRequest(context.Request));
        context.Request.ContentType = "Application/GRPC";
        Assert.True(UserRateLimitMiddleware.IsGrpcRequest(context.Request));
        Assert.False(UserRateLimitMiddleware.IsLimitedEndpoint(null));
    }
}
