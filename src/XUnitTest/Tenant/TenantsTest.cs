using Blocks.Genesis;

namespace XUnitTest.Tenant;

public class TenantsTests
{
    [Fact]
    public void Tenant_ShouldInitializeExpectedDefaults()
    {
        var tenant = new Blocks.Genesis.Tenant
        {
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
        Assert.Empty(tenant.Applications);
        Assert.False(tenant.IsRootTenant);
    }

    [Fact]
    public void Tenant_ShouldAllowSettingAndReadingAllProperties()
    {
        var jwtParameters = new JwtTokenParameters
        {
            Issuer = "issuer",
            Subject = "subject",
            Audiences = ["aud-1"],
            PublicCertificatePath = "cert-path",
            PublicCertificatePassword = "cert-pass",
            PrivateCertificatePassword = "private-pass",
            IssueDate = DateTime.UtcNow
        };

        var thirdParty = new ThirdPartyJwtTokenParameters
        {
            ProviderName = "auth0",
            Issuer = "https://issuer",
            Subject = "sub",
            Audiences = ["api"],
            PublicCertificatePath = "https://cert",
            JwksUrl = "https://jwks",
            PublicCertificatePassword = "pwd",
            CookieKey = "cookie"
        };

        var tenant = new Blocks.Genesis.Tenant
        {
            TenantId = "TENANT001",
            IsAcceptBlocksTerms = true,
            IsUseBlocksExclusively = true,
            Name = "Tenant One",
            DBName = "tenantdb",
            Applications = [new Blocks.Genesis.Applications { Domain = "https://tenant.app", CookieDomain = ".tenant.app", IsDomainVerified = true }, new Blocks.Genesis.Applications { Domain = "https://api.tenant.app" }],
            IsDisabled = true,
            DbConnectionString = "mongodb://localhost:27017",
            TenantSalt = "salt-value",
            JwtTokenParameters = jwtParameters,
            ThirdPartyJwtTokenParameters = thirdParty,
            IsRootTenant = true,
            Environment = "production",
            TenantGroupId = "group-1",
            ItemId = "item-1",
            CreatedDate = DateTime.UtcNow.AddDays(-1),
            LastUpdatedDate = DateTime.UtcNow,
            CreatedBy = "creator",
            Language = "en",
            LastUpdatedBy = "updater",
            Tags = ["tag-1"]
        };

        Assert.Equal("TENANT001", tenant.TenantId);
        Assert.True(tenant.IsAcceptBlocksTerms);
        Assert.True(tenant.IsUseBlocksExclusively);
        Assert.Equal("Tenant One", tenant.Name);
        Assert.Equal("tenantdb", tenant.DBName);
        Assert.Equal("https://tenant.app", tenant.Applications[0].Domain);
        Assert.Equal(2, tenant.Applications.Count);
        Assert.Equal(".tenant.app", tenant.Applications[0].CookieDomain);
        Assert.True(tenant.IsDisabled);
        Assert.Equal("mongodb://localhost:27017", tenant.DbConnectionString);
        Assert.Equal("salt-value", tenant.TenantSalt);
        Assert.Same(jwtParameters, tenant.JwtTokenParameters);
        Assert.Same(thirdParty, tenant.ThirdPartyJwtTokenParameters);
        Assert.True(tenant.IsRootTenant);
        Assert.True(tenant.Applications[0].IsDomainVerified);
        Assert.Equal("production", tenant.Environment);
        Assert.Equal("group-1", tenant.TenantGroupId);

        Assert.Equal("item-1", tenant.ItemId);
        Assert.Equal("creator", tenant.CreatedBy);
        Assert.Equal("en", tenant.Language);
        Assert.Equal("updater", tenant.LastUpdatedBy);
        Assert.Single(tenant.Tags);
    }

    [Fact]
    public void Tenant_Defaults_ShouldMatchExpectedShapes()
    {
        var tenant = new Blocks.Genesis.Tenant
        {
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

        Assert.Equal(32, tenant.TenantId.Length);
        Assert.Equal(tenant.TenantId.ToUpperInvariant(), tenant.TenantId);
        Assert.Equal(32, tenant.DBName.Length);
        Assert.Equal(32, tenant.TenantSalt.Length);
        Assert.Empty(tenant.Applications);
        Assert.False(tenant.IsDisabled);
        Assert.False(tenant.IsAcceptBlocksTerms);
        Assert.False(tenant.IsUseBlocksExclusively);
        Assert.NotNull(tenant.ThirdPartyJwtTokenParameters);
    }
}
