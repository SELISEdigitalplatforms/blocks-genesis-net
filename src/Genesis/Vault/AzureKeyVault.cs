using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;

namespace Blocks.Genesis
{
    public class AzureSecretProvider : ISecretProvider
    {
        private readonly SecretClient _client;
        private string _keyVaultUrl = string.Empty;
        private string _tenantId = string.Empty;
        private string _clientId = string.Empty;
        private string _clientSecret = string.Empty;

        public AzureSecretProvider()
        {
            ExtractValuesFromGlobalConfig(GetVaultConfig());
            _client = ConnectToAzureKeyVaultSecret();
        }

        public AzureSecretProvider(GenesisSecretOptions options)
        {
            var cloudConfig = GetVaultConfig();

            if (!string.IsNullOrWhiteSpace(options.Azure?.VaultUri))
            {
                cloudConfig["KeyVaultUrl"] = options.Azure.VaultUri;
            }

            if (!string.IsNullOrWhiteSpace(options.Azure?.TenantId))
            {
                cloudConfig["TenantId"] = options.Azure.TenantId;
            }

            if (!string.IsNullOrWhiteSpace(options.Azure?.ManagedIdentityClientId))
            {
                cloudConfig["ClientId"] = options.Azure.ManagedIdentityClientId;
            }

            ExtractValuesFromGlobalConfig(cloudConfig);
            _client = ConnectToAzureKeyVaultSecret();
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
                throw new InvalidOperationException("KeyVaultUrl is missing. Please check your environment configuration.");
            }

            cloudConfig.TryGetValue("TenantId", out _tenantId);
            cloudConfig.TryGetValue("ClientId", out _clientId);
            cloudConfig.TryGetValue("ClientSecret", out _clientSecret);
        }

        private SecretClient ConnectToAzureKeyVaultSecret()
        {
            if (!string.IsNullOrWhiteSpace(_tenantId) &&
                !string.IsNullOrWhiteSpace(_clientId) &&
                !string.IsNullOrWhiteSpace(_clientSecret))
            {
                var clientSecretCredential = new ClientSecretCredential(_tenantId, _clientId, _clientSecret);
                return new SecretClient(new Uri(_keyVaultUrl), clientSecretCredential);
            }

            var envConfig = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            var tenantId = string.IsNullOrWhiteSpace(_tenantId) ? envConfig["AZURE_TENANT_ID"] : _tenantId;
            var managedIdentityClientId = string.IsNullOrWhiteSpace(_clientId) ? envConfig["AZURE_CLIENT_ID"] : _clientId;

            var credentialOptions = new DefaultAzureCredentialOptions
            {
                TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId,
                ManagedIdentityClientId = string.IsNullOrWhiteSpace(managedIdentityClientId) ? null : managedIdentityClientId,
            };

            return new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential(credentialOptions));
        }

        public async Task<string?> GetAsync(string key)
        {
            try
            {
                var response = await _client.GetSecretAsync(key);
                return response.Value.Value;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error retrieving secret '{key}': {ex.Message}");
                return null;
            }
        }
    }
}
