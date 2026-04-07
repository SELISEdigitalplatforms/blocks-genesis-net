using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;

namespace Blocks.Genesis
{
    public class AzureKeyVault : IVault
    {
        private SecretClient _secretClient;
        private string _keyVaultUrl;

        public async Task<Dictionary<string, string>> ProcessSecretsAsync(List<string> keys)
        {
            ExtractValuesFromGlobalConfig(GetVaultConfig());
            ConnectToAzureKeyVaultSecret();
            return await GetSecretsFromVaultAsync(keys);
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
            if (!cloudConfig.TryGetValue("KeyVaultUrl", out _keyVaultUrl) || string.IsNullOrWhiteSpace(_keyVaultUrl))
            {
                throw new InvalidOperationException("Required Azure config value 'KeyVaultUrl' is missing. Please check your environment configuration.");
            }
        }

        private void ConnectToAzureKeyVaultSecret()
        {
            // Prefer Azure CLI locally and fall back to Managed Identity in container/server deployments.
            var credential = new ChainedTokenCredential(
                new AzureCliCredential(),
                new ManagedIdentityCredential());

            _secretClient = new SecretClient(new Uri(_keyVaultUrl), credential);
        }

        private async Task<Dictionary<string, string>> GetSecretsFromVaultAsync(List<string> keys)
        {
            var secrets = new Dictionary<string, string>();

            foreach (var key in keys)
            {
                var secretValue = await GetSecretFromKeyVaultAsync(key);
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
                var secret = await _secretClient.GetSecretAsync(key);
                return secret.Value.Value;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error retrieving secret '{key}': {e.Message}");
                return string.Empty;
            }
        }
    }
}
