using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace Blocks.Genesis;

public class AzureKeyVault : IVault
{
    private SecretClient _secretClient = default!;
    private string _keyVaultUrl = string.Empty;
    private string? _clientId = string.Empty;
    private string? _clientSecret = string.Empty;
    private string? _tenantId = string.Empty;
    private const string _azureVaultUrl = "https://vault.azure.net/.default";
    public async Task<Dictionary<string, string>> ProcessSecretsAsync(List<string> keys)
    {
        ExtractValuesFromGlobalConfig(GetVaultConfig());
         await ConnectToAzureKeyVaultSecret().ConfigureAwait(false);
        return await GetSecretsFromVaultAsync(keys).ConfigureAwait(false);
    }

    public static Dictionary<string, string> GetVaultConfig()
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var keyVaultConfig = new Dictionary<string, string>();
        configuration.GetSection(BlocksConstants.KeyVault).Bind(keyVaultConfig);

        return keyVaultConfig;
    }

    private void ExtractValuesFromGlobalConfig(Dictionary<string, string> cloudConfig)
    {
        if (!cloudConfig.TryGetValue("KeyVaultUrl", out var keyVaultUrl) || string.IsNullOrWhiteSpace(keyVaultUrl))
        {
            throw new InvalidOperationException("Required Azure config value 'KeyVaultUrl' is missing. Please check your environment configuration.");
        }

        _keyVaultUrl = keyVaultUrl;
        cloudConfig.TryGetValue("ClientId", out _clientId);
        cloudConfig.TryGetValue("ClientSecret", out _clientSecret);
        cloudConfig.TryGetValue("TenantId", out _tenantId);
    }

    // Seams for tests: default to the real Azure credential chain and client.
    private List<Func<TokenCredential?>>? _credentialFactories;
    private Func<Uri, TokenCredential, SecretClient> _secretClientFactory = (uri, credential) => new SecretClient(uri, credential);

    // Swaps the credential chain and secret client factory so the connect
    // path can be exercised without live Azure.
    internal void OverrideConnectionSeams(
        List<Func<TokenCredential?>> credentialFactories,
        Func<Uri, TokenCredential, SecretClient> secretClientFactory)
    {
        _credentialFactories = credentialFactories;
        _secretClientFactory = secretClientFactory;
    }

    private async Task ConnectToAzureKeyVaultSecret()
    {
        var credentialFactories = _credentialFactories ??
        [
            () => new DefaultAzureCredential(),
            () => HasClientSecretConfig()
                ? new ClientSecretCredential(_tenantId, _clientId, _clientSecret)
                : null
        ];

        foreach (var makeCredential in credentialFactories)
        {
            var credential = makeCredential();
            if (credential is null)
            {
                continue;
            }

            if (await CanAcquireTokenAsync(credential).ConfigureAwait(false))
            {
                _secretClient = _secretClientFactory(new Uri(_keyVaultUrl), credential);
                return;
            }
        }

        throw new InvalidOperationException("Unable to authenticate to Key Vault: no credential succeeded.");
    }
    private static async Task<bool> CanAcquireTokenAsync(TokenCredential credential)
    {
        try
        {
            var context = new TokenRequestContext(new[] { _azureVaultUrl });
            await credential.GetTokenAsync(context, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (AuthenticationFailedException)
        {
            return false;
        }
    }
    private async Task<Dictionary<string, string>> GetSecretsFromVaultAsync(List<string> keys)
    {
        var secrets = new Dictionary<string, string>();

        foreach (var key in keys)
        {
            var secretValue = await GetSecretFromKeyVaultAsync(key).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(secretValue))
            {
                secrets.Add(key, secretValue);
            }
        }

        return secrets;
    }
   
    private async Task<string> GetSecretFromKeyVaultAsync(string key)
    {   
        try
        {
            var secret = await _secretClient.GetSecretAsync(key).ConfigureAwait(false);
            return secret.Value.Value;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Error retrieving secret '{Key}' from Key Vault.", key);
            return string.Empty;
        }
    }
    private bool HasClientSecretConfig() =>
        !string.IsNullOrWhiteSpace(_clientId) && !string.IsNullOrWhiteSpace(_clientSecret) &&!string.IsNullOrWhiteSpace(_tenantId);
}
