using Blocks.Genesis;
using Microsoft.Extensions.DependencyInjection;

namespace XUnitTest.Vault;

public class VaultTests
{
    [Fact]
    public void AddGenesisSecrets_ShouldRegisterLocalSecretProvider_ForOnPremMode()
    {
        var services = new ServiceCollection();
        services.AddGenesisSecrets(opt => opt.Mode = SecretMode.OnPrem);
        var provider = services.BuildServiceProvider();

        Assert.IsType<LocalSecretProvider>(provider.GetRequiredService<ISecretProvider>());
    }

    [Fact]
    public void AddGenesisSecrets_ShouldRegisterAzureSecretProvider_ForAzureMode()
    {
        var services = new ServiceCollection();
        services.AddGenesisSecrets(opt =>
        {
            opt.Mode = SecretMode.Azure;
            opt.Azure = new AzureSecretOptions { VaultUri = "https://fake.vault.azure.net" };
        });
        var provider = services.BuildServiceProvider();

        Assert.IsType<AzureSecretProvider>(provider.GetRequiredService<ISecretProvider>());
    }

    [Fact]
    public void AddGenesisSecrets_ShouldRegisterISecretProvider_ForPlatformMode()
    {
        var services = new ServiceCollection();
        services.AddGenesisSecrets(opt =>
        {
            opt.Mode = SecretMode.Platform;
            opt.Platform = new PlatformOptions
            {
                BaseUrl = "https://platform.example.com",
                ClientId = "test-client",
                XBlocksKey = "test-key"
            };
        });

        Assert.Contains(services, sd => sd.ServiceType == typeof(ISecretProvider));
    }

    [Fact]
    public void AddGenesisSecrets_ShouldThrow_ForUnknownMode()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() =>
            services.AddGenesisSecrets(opt => opt.Mode = (SecretMode)99));
    }
}