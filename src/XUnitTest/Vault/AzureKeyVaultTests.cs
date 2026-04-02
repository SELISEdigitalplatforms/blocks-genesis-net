using Blocks.Genesis;

namespace XUnitTest.Vault;

public class AzureKeyVaultTests
{
    [Fact]
    public void Constructor_ShouldSucceed_WhenVaultUriIsInEnvironment()
    {
        var previousVault = Environment.GetEnvironmentVariable("KeyVault__KeyVaultUrl");
        try
        {
            Environment.SetEnvironmentVariable("KeyVault__KeyVaultUrl", "https://fake.vault.azure.net");
            var provider = new AzureSecretProvider();

            Assert.NotNull(provider);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KeyVault__KeyVaultUrl", previousVault);
        }
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenVaultUriMissingInEnvironment()
    {
        var previousVault = Environment.GetEnvironmentVariable("KeyVault__KeyVaultUrl");
        try
        {
            Environment.SetEnvironmentVariable("KeyVault__KeyVaultUrl", null);
            Assert.Throws<InvalidOperationException>(() => new AzureSecretProvider());
        }
        finally
        {
            Environment.SetEnvironmentVariable("KeyVault__KeyVaultUrl", previousVault);
        }
    }

    [Fact]
    public void Constructor_ShouldAllowTenantAndClientIdFromEnvironment_ForDefaultCredentialFlow()
    {
        var previousVault = Environment.GetEnvironmentVariable("KeyVault__KeyVaultUrl");
        var previousTenant = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");
        var previousClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        try
        {
            Environment.SetEnvironmentVariable("KeyVault__KeyVaultUrl", "https://fake.vault.azure.net");
            Environment.SetEnvironmentVariable("AZURE_TENANT_ID", "11111111-1111-1111-1111-111111111111");
            Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", "22222222-2222-2222-2222-222222222222");

            var provider = new AzureSecretProvider();

            Assert.NotNull(provider);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KeyVault__KeyVaultUrl", previousVault);
            Environment.SetEnvironmentVariable("AZURE_TENANT_ID", previousTenant);
            Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", previousClientId);
        }
    }

}
