using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace Blocks.Genesis
{
    public class AzureKeyVault : IVault
    {
        private SecretClient _secretClient = default!;
        private string _keyVaultUrl = string.Empty;

        public async Task<Dictionary<string, string>> ProcessSecretsAsync(List<string> keys)
        {
            ExtractValuesFromGlobalConfig(GetVaultConfig());
            ConnectToAzureKeyVaultSecret();
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
        }

        private void ConnectToAzureKeyVaultSecret()
        {
            // DefaultAzureCredential covers local dev (az login, VS, VS Code) and
            // production (Managed Identity, Workload Identity) in a single credential.
            var credential = new DefaultAzureCredential();

            _secretClient = new SecretClient(new Uri(_keyVaultUrl), credential);
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
    }
}
