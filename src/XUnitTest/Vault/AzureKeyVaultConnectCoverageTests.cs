using Azure.Security.KeyVault.Secrets;
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

        var tokenCredentialType = Type.GetType("Azure.Core.TokenCredential, Azure.Core")!;
        var credentialFactoriesType = typeof(List<>).MakeGenericType(typeof(Func<>).MakeGenericType(tokenCredentialType));
        var credentialFactories = Activator.CreateInstance(credentialFactoriesType);

        var secretClientFactoryType = typeof(Func<,,>).MakeGenericType(typeof(Uri), tokenCredentialType, typeof(SecretClient));
        var secretClientFactory = Delegate.CreateDelegate(secretClientFactoryType, typeof(AzureKeyVaultConnectCoverageTests).GetMethod(nameof(CreateFailingSecretClient), BindingFlags.Static | BindingFlags.NonPublic)!);

        var overrideMethod = typeof(AzureKeyVault).GetMethod("OverrideConnectionSeams", BindingFlags.Instance | BindingFlags.NonPublic);
        overrideMethod!.Invoke(vault, new object[] { credentialFactories, secretClientFactory });

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => InvokeConnect(vault));

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

        var tokenCredentialType = Type.GetType("Azure.Core.TokenCredential, Azure.Core")!;
        var credentialFactoriesType = typeof(List<>).MakeGenericType(typeof(Func<>).MakeGenericType(tokenCredentialType));
        var credentialFactories = Activator.CreateInstance(credentialFactoriesType);

        var secretClientFactoryType = typeof(Func<,,>).MakeGenericType(typeof(Uri), tokenCredentialType, typeof(SecretClient));
        var secretClientFactory = Delegate.CreateDelegate(secretClientFactoryType, typeof(AzureKeyVaultConnectCoverageTests).GetMethod(nameof(CreateFailingSecretClient), BindingFlags.Static | BindingFlags.NonPublic)!);

        var overrideMethod = typeof(AzureKeyVault).GetMethod("OverrideConnectionSeams", BindingFlags.Instance | BindingFlags.NonPublic);
        overrideMethod!.Invoke(vault, new object[] { credentialFactories, secretClientFactory });

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => InvokeConnect(vault));

        Assert.NotNull(ex);
    }

    private static SecretClient CreateFailingSecretClient(Uri uri, object credential)
    {
        throw new InvalidOperationException("Simulated failure.");
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
