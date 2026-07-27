using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using Moq;
using System.Reflection;

namespace XUnitTest.Middlewares;

public class TenantValidationMiddlewareCoverageTests
{
    [Fact]
    public void Constructor_ShouldNormalizeConfiguredPrefixes_AndIgnoreBlankOnes()
    {
        var middleware = CreateMiddleware([" /custom/ ", "   ", null!, "grpc"]);

        var prefixes = GetPrefixes(middleware);

        Assert.Contains("custom", prefixes);
        Assert.Contains("grpc", prefixes);
        Assert.Contains("api", prefixes);
        Assert.Equal(3, prefixes.Count);
    }

    [Theory]
    [InlineData("/custom", true)]
    [InlineData("/custom/orders", true)]
    [InlineData("/customized", false)]
    [InlineData("/elsewhere", false)]
    [InlineData("/", false)]
    public void RequiresTenantValidation_ShouldMatchConfiguredPrefixes(string path, bool expected)
    {
        var middleware = CreateMiddleware(["custom"]);

        var method = typeof(TenantValidationMiddleware).GetMethod("RequiresTenantValidation", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = (bool)method!.Invoke(middleware, [new PathString(path)])!;

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Authorization", true)]
    [InlineData("X-Api-Token", true)]
    [InlineData("client_secret", true)]
    [InlineData("user-password", true)]
    [InlineData("Accept", false)]
    public void IsSensitiveKey_ShouldFlagCredentialBearingHeaders(string? key, bool expected)
    {
        var method = typeof(TenantValidationMiddleware).GetMethod("IsSensitiveKey", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = (bool)method!.Invoke(null, [key])!;

        Assert.Equal(expected, result);
    }

    private static TenantValidationMiddleware CreateMiddleware(string[] configuredPrefixes)
    {
        return new TenantValidationMiddleware(
            _ => Task.CompletedTask,
            new Mock<ITenants>().Object,
            new Mock<ICryptoService>().Object,
            configuredPrefixes);
    }

    private static HashSet<string> GetPrefixes(TenantValidationMiddleware middleware)
    {
        var field = typeof(TenantValidationMiddleware).GetField("_tenantValidationPrefixes", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (HashSet<string>)field!.GetValue(middleware)!;
    }
}
