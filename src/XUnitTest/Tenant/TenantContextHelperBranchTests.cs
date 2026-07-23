using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using System.Reflection;

namespace XUnitTest.Tenant;

/// <summary>Branch coverage for the internal <c>TenantContextHelper</c> form/localhost/application-domain resolution.</summary>
public class TenantContextHelperBranchTests
{
    [Fact]
    public void ResolveTenantIdFromForm_ShouldReturnValue_WhenPresent_ElseNull()
    {
        var form = new FormCollection(new Dictionary<string, StringValues> { [BlocksConstants.BlocksKey] = "tenant-form" });
        Assert.Equal("tenant-form", (string?)Call("ResolveTenantIdFromForm", form));

        Assert.Null((string?)Call("ResolveTenantIdFromForm", new FormCollection([])));
        Assert.Null((string?)Call("ResolveTenantIdFromForm", new FormCollection(new Dictionary<string, StringValues> { [BlocksConstants.BlocksKey] = "" })));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("app.local", false)]
    public void IsLocalhostHost_ShouldDetectLoopback(string? host, bool expected)
    {
        Assert.Equal(expected, (bool)Call("IsLocalhostHost", host)!);
    }

    [Fact]
    public void ResolveApplicationDomain_ShouldReturnMatchingApplicationDomain_ForBrowserOrigin()
    {
        var tenant = new Blocks.Genesis.Tenant
        {
            TenantId = "t1",
            Applications = [new Blocks.Genesis.Applications { Domain = "app.local" }],
            DbConnectionString = "mongodb://localhost:27017",
            JwtTokenParameters = new JwtTokenParameters
            {
                Issuer = "i", Subject = "s", Audiences = [],
                PublicCertificatePath = "p", PublicCertificatePassword = "", PrivateCertificatePassword = "", IssueDate = DateTime.UtcNow
            }
        };

        Assert.Equal("app.local", (string)Call("ResolveApplicationDomain", tenant, "http://app.local", null)!);
    }

    private static object? Call(string method, params object?[] args)
    {
        var type = typeof(BlocksContext).Assembly.GetType("Blocks.Genesis.TenantContextHelper")!;
        return type.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, args);
    }
}
