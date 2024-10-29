using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using System.Reflection;

namespace Blocks.Genesis
{
    public class AzureKeyVault : ICloudVault
    {
        private SecretClient _secretClient;
        private string _keyVaultUrl;
        private string _tenantId;
        private string _clientId;
        private string _clientSecret;
        private BlocksSecret _blocksSecret;

        public async Task<BlocksSecret> ProcessSecrets(BlocksSecret blocksSecret, Dictionary<string, string> cloudConfig)
        {
            _blocksSecret = blocksSecret;

            ExtractValuesFromGlobalConfig(cloudConfig);

            ConnectAzureKeyVault();

            await FormatGlobalConfigFromAzureKeyVault();

            return _blocksSecret;
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

        public async Task<bool> FormatGlobalConfigFromAzureKeyVault()
        {
            PropertyInfo[] properties = typeof(BlocksSecret).GetProperties();

            foreach (PropertyInfo property in properties)
            {
                string propertyName = property.Name;
                string retrievedValue = await GetDataFromKeyVault(propertyName);

                if (!string.IsNullOrWhiteSpace(retrievedValue))
                {
                    object convertedValue = ConvertValue(retrievedValue, property.PropertyType);

                    _blocksSecret.UpdateProperty(propertyName, convertedValue);
                }
            }

            return true;
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

        public static object ConvertValue(string value, Type targetType)
        {
            if (targetType != typeof(string))
            {
                try
                {
                    return Convert.ChangeType(value, targetType);
                }
                catch (Exception)
                {
                    // Handle conversion exceptions
                }
            }
            return value;
        }
    }

}
