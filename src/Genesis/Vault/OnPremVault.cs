using Microsoft.Extensions.Configuration;

namespace Blocks.Genesis
{
    public class OnPremVault : IVault
    {
        public Task<Dictionary<string, string>> ProcessSecretsAsync(List<string> keys)
        {
            return Task.FromResult(GetVaultValues());
        }

        public static Dictionary<string, string> GetVaultValues()
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            var keyVaultConfig = new Dictionary<string, string>();
            configuration.GetSection("BlocksSecret").Bind(keyVaultConfig);

            return keyVaultConfig;
        }
    }
}