using Azure.Identity;
using Azure.Security.KeyVault.Certificates;
using Azure.Security.KeyVault.Secrets;

namespace Blocks.Genesis
{
    public class AzureKeyVault : ICloudVault
    {
        private SecretClient _secretClient;
        private CertificateClient _certificateClient;
        private string _keyVaultUrl;
        private string _tenantId;
        private string _clientId;
        private string _clientSecret;

        public async Task<Dictionary<string, string>> ProcessSecretsAsync(List<string> keys, Dictionary<string, string> cloudConfig)
        {
            ExtractValuesFromGlobalConfig(cloudConfig);
            ConnectToAzureKeyVaultSecret();
            return await GetSecretsFromVaultAsync(keys);
        }

        public async Task<string> ProcessSecretAsync(string key, Dictionary<string, string> cloudConfig)
        {
            ExtractValuesFromGlobalConfig(cloudConfig);
            ConnectToAzureKeyVaultSecret();
            return await GetSecretFromKeyVaultAsync(key);
        }

        public async Task<byte[]> ProcessCertificateAsync(string certificateName, Dictionary<string, string> cloudConfig)
        {
            ExtractValuesFromGlobalConfig(cloudConfig);
            ConnectToAzureKeyVaultCertificate();
            return await GetCertificateFromKeyVaultAsync(certificateName);
        }

        private void ExtractValuesFromGlobalConfig(Dictionary<string, string> cloudConfig)
        {
            if (!cloudConfig.TryGetValue("KeyVaultUrl", out _keyVaultUrl) ||
                !cloudConfig.TryGetValue("TenantId", out _tenantId) ||
                !cloudConfig.TryGetValue("ClientId", out _clientId) ||
                !cloudConfig.TryGetValue("ClientSecret", out _clientSecret))
            {
                throw new Exception("One or more required Azure config values are missing. Please check your environment configuration.");
            }
        }

        private void ConnectToAzureKeyVaultSecret()
        {
            var credential = new ClientSecretCredential(_tenantId, _clientId, _clientSecret);
            _secretClient = new SecretClient(new Uri(_keyVaultUrl), credential);
        }

        private void ConnectToAzureKeyVaultCertificate()
        {
            var credential = new ClientSecretCredential(_tenantId, _clientId, _clientSecret);
            _certificateClient = new CertificateClient(new Uri(_keyVaultUrl), credential);
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

        private async Task<byte[]> GetCertificateFromKeyVaultAsync(string certificateName)
        {
            try
            {
                var certificate = await _certificateClient.GetCertificateAsync(certificateName);
                return certificate.Value.Cer;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error retrieving certificate '{certificateName}': {e.Message}");
                return null;
            }
        }
    }
}
