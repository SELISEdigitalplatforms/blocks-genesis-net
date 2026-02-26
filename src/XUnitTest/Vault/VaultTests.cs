using Blocks.Genesis;

namespace XUnitTest.Vault;

public class VaultTests
{
    [Fact]
    public void GetCloudVault_ShouldReturnOnPremVault_ForOnPremType()
    {
        var vault = Blocks.Genesis.Vault.GetCloudVault(VaultType.OnPrem);

        Assert.IsType<OnPremVault>(vault);
    }

    [Fact]
    public void GetCloudVault_ShouldReturnAzureKeyVault_ForAzureType()
    {
        var vault = Blocks.Genesis.Vault.GetCloudVault(VaultType.Azure);

        Assert.IsType<AzureKeyVault>(vault);
    }

    [Fact]
    public void GetCloudVault_ShouldThrow_ForUnknownType()
    {
        Assert.Throws<Exception>(() => Blocks.Genesis.Vault.GetCloudVault(VaultType.Unknown));
    }
}