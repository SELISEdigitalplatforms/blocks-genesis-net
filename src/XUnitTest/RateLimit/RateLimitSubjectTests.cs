using Blocks.Genesis;
using System.Security.Claims;

namespace XUnitTest.RateLimit;

public class RateLimitSubjectTests
{
    internal static ClaimsPrincipal Principal(string? userId = null, string? clientId = null, string? tenantId = null, bool authenticated = true)
    {
        var claims = new List<Claim>();
        if (userId is not null) claims.Add(new Claim(BlocksContext.USER_ID_CLAIM, userId));
        if (clientId is not null) claims.Add(new Claim(BlocksContext.CLIENT_ID_CLAIM, clientId));
        if (tenantId is not null) claims.Add(new Claim(BlocksContext.TENANT_ID_CLAIM, tenantId));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticated ? "Test" : null));
    }

    [Fact]
    public void TryResolve_UsesUser_WhenUserIdPresent()
    {
        Assert.True(RateLimitSubject.TryResolve(Principal("u1", tenantId: "t1"), out var subject));
        Assert.Equal("ratelimit:svc-a:t1:user:u1", subject.BuildKey("svc-a"));
    }

    [Fact]
    public void TryResolve_UsesClient_WhenOnlyClientIdPresent() // H6
    {
        Assert.True(RateLimitSubject.TryResolve(Principal(clientId: "c9", tenantId: "t1"), out var subject));
        Assert.Equal(RateLimitSubject.ClientType, subject.Type);
        Assert.Equal("ratelimit:svc-a:t1:client:c9", subject.BuildKey("svc-a"));
    }

    [Fact]
    public void TryResolve_PrefersUser_WhenBothPresent() // H7
    {
        Assert.True(RateLimitSubject.TryResolve(Principal("u1", "c9", "t1"), out var subject));
        Assert.Equal(RateLimitSubject.UserType, subject.Type);
        Assert.Equal("u1", subject.Id);
    }

    [Fact]
    public void TryResolve_ReturnsFalse_WhenNoSubjectOrNotAuthenticated() // C2, C3
    {
        Assert.False(RateLimitSubject.TryResolve(Principal(tenantId: "t1"), out _));
        Assert.False(RateLimitSubject.TryResolve(Principal("u1", authenticated: false), out _));
        Assert.False(RateLimitSubject.TryResolve(null, out _));
        Assert.False(RateLimitSubject.TryResolve(Principal(" ", " "), out _));
    }

    [Fact]
    public void BuildKey_WritesDashForEmptyTenant()
    {
        Assert.True(RateLimitSubject.TryResolve(Principal("u1"), out var subject));
        Assert.Equal("ratelimit:svc-a:-:user:u1", subject.BuildKey("svc-a"));
    }
}
