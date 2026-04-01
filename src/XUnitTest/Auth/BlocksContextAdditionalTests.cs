using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace XUnitTest.Auth;

public class BlocksContextAdditionalTests : IDisposable
{
    public BlocksContextAdditionalTests()
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
    public void Create_ShouldReturnContextWithAllProperties()
    {
        var context = BlocksContext.Create(
            "t1", ["admin"], "u1", true, "/api", "org1",
            DateTime.MinValue, "e@e.com", ["read"], "user1",
            "123", "John", "token", "refresh", "t1");

        Assert.Equal("t1", context.TenantId);
        Assert.Contains("admin", context.Roles);
        Assert.Equal("u1", context.UserId);
        Assert.True(context.IsAuthenticated);
        Assert.Equal("/api", context.RequestUri);
        Assert.Equal("org1", context.OrganizationId);
        Assert.Equal("e@e.com", context.Email);
        Assert.Contains("read", context.Permissions);
        Assert.Equal("user1", context.UserName);
        Assert.Equal("123", context.PhoneNumber);
        Assert.Equal("John", context.DisplayName);
        Assert.Equal("token", context.OAuthToken);
        Assert.Equal("refresh", context.RefreshToken);
        Assert.Equal("t1", context.ActualTenantId);
    }

    [Fact]
    public void Create_ShouldHandleNullParameters()
    {
        var context = BlocksContext.Create(
            null, null, null, false, null, null,
            DateTime.MinValue, null, null, null,
            null, null, null, null, null);

        Assert.Equal(string.Empty, context.TenantId);
        Assert.Empty(context.Roles);
        Assert.Equal(string.Empty, context.UserId);
        Assert.False(context.IsAuthenticated);
    }

    [Fact]
    public void SetContext_ThenGetContext_ShouldReturnSameContext()
    {
        var ctx = BlocksContext.Create(
            "tenant-x", [], "u-x", true, "/test", "org-x",
            DateTime.MinValue, "x@x.com", [], "ux",
            "", "", "", "", "tenant-x");

        BlocksContext.SetContext(ctx);
        var retrieved = BlocksContext.GetContext();

        Assert.NotNull(retrieved);
        Assert.Equal("tenant-x", retrieved!.TenantId);
        Assert.Equal("u-x", retrieved.UserId);
    }

    [Fact]
    public void ClearContext_ShouldRemoveStoredContext()
    {
        var ctx = BlocksContext.Create(
            "tenant-y", [], "u-y", true, "/", "org",
            DateTime.MinValue, "", [], "", "", "", "", "", "tenant-y");

        BlocksContext.SetContext(ctx);
        BlocksContext.ClearContext();

        var result = BlocksContext.GetContext();
        // In test mode, ClearContext sets to null, GetContext returns null or default
        Assert.True(result == null || string.IsNullOrEmpty(result.TenantId));
    }

    [Fact]
    public void ExecuteInContext_Action_ShouldRunWithinGivenContext()
    {
        var ctx = BlocksContext.Create(
            "temp-tenant", [], "temp-user", true, "/", "",
            DateTime.MinValue, "", [], "", "", "", "", "", "temp-tenant");

        string? capturedTenantId = null;

        BlocksContext.ExecuteInContext(ctx, () =>
        {
            capturedTenantId = BlocksContext.GetContext()?.TenantId;
        });

        Assert.Equal("temp-tenant", capturedTenantId);
    }

    [Fact]
    public void ExecuteInContext_Func_ShouldReturnResult()
    {
        var ctx = BlocksContext.Create(
            "func-tenant", [], "u", false, "/", "",
            DateTime.MinValue, "", [], "", "", "", "", "", "func-tenant");

        var result = BlocksContext.ExecuteInContext(ctx, () =>
        {
            return BlocksContext.GetContext()?.TenantId ?? "none";
        });

        Assert.Equal("func-tenant", result);
    }

    [Fact]
    public void ExecuteInContext_ShouldRestorePreviousContext()
    {
        var original = BlocksContext.Create(
            "original", [], "", false, "", "",
            DateTime.MinValue, "", [], "", "", "", "", "", "");
        var temp = BlocksContext.Create(
            "temporary", [], "", false, "", "",
            DateTime.MinValue, "", [], "", "", "", "", "", "");

        BlocksContext.SetContext(original);

        BlocksContext.ExecuteInContext(temp, () =>
        {
            Assert.Equal("temporary", BlocksContext.GetContext()?.TenantId);
        });

        Assert.Equal("original", BlocksContext.GetContext()?.TenantId);
    }

    [Fact]
    public void ExecuteInContext_Action_ShouldThrowOnNullContext()
    {
        Assert.Throws<ArgumentNullException>(() =>
            BlocksContext.ExecuteInContext(null!, () => { }));
    }

    [Fact]
    public void ExecuteInContext_Action_ShouldThrowOnNullAction()
    {
        var ctx = BlocksContext.Create("t", [], "", false, "", "",
            DateTime.MinValue, "", [], "", "", "", "", "", "");

        Assert.Throws<ArgumentNullException>(() =>
            BlocksContext.ExecuteInContext(ctx, (Action)null!));
    }

    [Fact]
    public void ExecuteInContext_Func_ShouldThrowOnNullContext()
    {
        Assert.Throws<ArgumentNullException>(() =>
            BlocksContext.ExecuteInContext<string>(null!, () => "x"));
    }

    [Fact]
    public void ExecuteInContext_Func_ShouldThrowOnNullFunc()
    {
        var ctx = BlocksContext.Create("t", [], "", false, "", "",
            DateTime.MinValue, "", [], "", "", "", "", "", "");

        Assert.Throws<ArgumentNullException>(() =>
            BlocksContext.ExecuteInContext(ctx, (Func<string>)null!));
    }

    [Fact]
    public void JsonSerialization_ShouldRoundTrip()
    {
        var original = BlocksContext.Create(
            "serde-tenant", ["role1"], "serde-user", true, "/api", "org-s",
            DateTime.MinValue, "s@e.com", ["write"], "suser",
            "555", "Serde User", "tok", "ref", "serde-tenant");

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<BlocksContext>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("serde-tenant", deserialized!.TenantId);
        Assert.Equal("serde-user", deserialized.UserId);
        Assert.True(deserialized.IsAuthenticated);
    }

    [Fact]
    public void SetContext_WithNull_ShouldClearContext()
    {
        var ctx = BlocksContext.Create("t", [], "u", false, "", "",
            DateTime.MinValue, "", [], "", "", "", "", "", "");
        BlocksContext.SetContext(ctx);
        BlocksContext.SetContext(null);

        var result = BlocksContext.GetContext();
        Assert.True(result == null || string.IsNullOrEmpty(result.UserId));
    }

    [Fact]
    public void IsTestMode_ShouldBeSettable()
    {
        Assert.True(BlocksContext.IsTestMode);
        BlocksContext.IsTestMode = false;
        Assert.False(BlocksContext.IsTestMode);
        BlocksContext.IsTestMode = true;
    }
}
