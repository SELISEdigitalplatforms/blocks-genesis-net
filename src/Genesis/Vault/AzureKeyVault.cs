using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace Blocks.Genesis
{
    public class AzureKeyVault : ICloudVault
    {
        private SecretClient _secretClient;
        private string _keyVaultUrl;
        private string _tenantId;
        private string _clientId;
        private string _clientSecret;

        public async Task<Dictionary<string, string>> ProcessSecrets(List<string> keys, Dictionary<string, string> cloudConfig)
        {
            ExtractValuesFromGlobalConfig(cloudConfig);

            ConnectAzureKeyVault();

            return await GetSecretFromVault(keys);
        }

        public async Task<string> ProcessSecret(string key, Dictionary<string, string> cloudConfig)
        {
            ExtractValuesFromGlobalConfig(cloudConfig);

            ConnectAzureKeyVault();

            return await GetDataFromKeyVault(key);
        }

        public bool ExtractValuesFromGlobalConfig(Dictionary<string, string> cloudConfig)
        {
            try
            {
                _keyVaultUrl = cloudConfig["KeyVaultUrl"];
                _tenantId = cloudConfig["TenantId"];
                _clientId = cloudConfig["ClientId"];
                _clientSecret = cloudConfig["ClientSecret"];

                return true;
            }
            catch (Exception)
            {
                throw new Exception("One of the AZURE config or \"CloudConfig\" is missing. Please check your env file or windows env variables");
            }
        }

        public bool ConnectAzureKeyVault()
        {
            _secretClient = new SecretClient(new Uri(_keyVaultUrl), new ClientSecretCredential(_tenantId, _clientId, _clientSecret));

            return true;
        }

        public async Task<Dictionary<string, string>> GetSecretFromVault(List<string> keys)
        {
            var secrets = new Dictionary<string, string>();

            foreach (string key in keys)
            {
                secrets.Add(await GetDataFromKeyVault(key), key);
            }

            return secrets;
        }

        public async Task<string> GetDataFromKeyVault(string propertyName)
        {
            try
            {
                var secret = await _secretClient.GetSecretAsync(propertyName);

                return secret.Value.Value;
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                return string.Empty;
            }
        }


    }

}
