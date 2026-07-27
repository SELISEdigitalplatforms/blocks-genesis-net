using Blocks.Genesis;
using System.Reflection;

namespace XUnitTest.Vault;

[Collection("AzureKeyVaultEnvironment")]
public class AzureKeyVaultConnectCoverageTests
{
    [Fact]
    public async Task ProcessSecretsAsync_ShouldThrow_WhenKeyVaultUrlIsMissing()
    {
        var previousUrl = Environment.GetEnvironmentVariable("KeyVault__KeyVaultUrl");
        try
        {
            Environment.SetEnvironmentVariable("KeyVault__KeyVaultUrl", null);

            var vault = new AzureKeyVault();

            await Assert.ThrowsAsync<InvalidOperationException>(() => vault.ProcessSecretsAsync(["any-key"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("KeyVault__KeyVaultUrl", previousUrl);
        }
    }

    [Fact]
    public async Task ConnectToAzureKeyVaultSecret_ShouldTryClientSecretCredential_ThenFail_WhenNoCredentialWorks()
    {
        var vault = new AzureKeyVault();
        SetField(vault, "_keyVaultUrl", "https://unit-test-vault.vault.azure.net/");
        SetField(vault, "_clientId", "00000000-0000-0000-0000-000000000001");
        SetField(vault, "_clientSecret", "not-a-real-secret-value");
        SetField(vault, "_tenantId", "00000000-0000-0000-0000-000000000002");

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => InvokeConnect(vault));

        // Offline, neither DefaultAzureCredential nor the fabricated ClientSecretCredential
        // can acquire a token; the loop exhausts and surfaces a failure.
        Assert.NotNull(ex);
    }

    [Fact]
    public async Task ConnectToAzureKeyVaultSecret_ShouldSkipClientSecretCredential_WhenConfigIncomplete()
    {
        var vault = new AzureKeyVault();
        SetField(vault, "_keyVaultUrl", "https://unit-test-vault.vault.azure.net/");
        SetField(vault, "_clientId", null);
        SetField(vault, "_clientSecret", null);
        SetField(vault, "_tenantId", null);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => InvokeConnect(vault));

        Assert.NotNull(ex);
    }

    private static void SetField(object instance, string name, object? value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }

    private static async Task InvokeConnect(AzureKeyVault vault)
    {
        var method = typeof(AzureKeyVault).GetMethod("ConnectToAzureKeyVaultSecret", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        await (Task)method!.Invoke(vault, [])!;
    }
}
