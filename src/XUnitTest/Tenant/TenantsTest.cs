using Blocks.Genesis;

namespace XUnitTest.Tenant;

public class TenantsTests
{
    [Fact]
    public void Tenant_ShouldInitializeExpectedDefaults()
    {
        var tenant = new Blocks.Genesis.Tenant
        {
            ApplicationDomain = "sample",
            DbConnectionString = "mongodb://localhost:27017",
            JwtTokenParameters = new JwtTokenParameters
            {
                Issuer = "issuer",
                Subject = "subject",
                Audiences = [],
                PublicCertificatePath = "path",
                PublicCertificatePassword = "password",
                PrivateCertificatePassword = "private",
                IssueDate = DateTime.UtcNow
            }
        };

        Assert.NotNull(tenant.TenantId);
        Assert.NotNull(tenant.DBName);
        Assert.NotNull(tenant.TenantSalt);
        Assert.Empty(tenant.AllowedDomains);
        Assert.False(tenant.IsRootTenant);
    }
}
