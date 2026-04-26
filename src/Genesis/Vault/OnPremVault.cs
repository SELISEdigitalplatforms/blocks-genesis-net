using Blocks.Genesis.Utilities;
using DotNetEnv;
using Microsoft.Extensions.Configuration;

namespace Blocks.Genesis
{
    public class OnPremVault : IVault
    {
        public async Task<Dictionary<string, string>> ProcessSecretsAsync(List<string> keys, IConfiguration configuration)
        {
            return await Task.FromResult(GetVaultValues(configuration));
        }

        public static Dictionary<string, string> GetVaultValues(IConfiguration configuration)
        {
            var config = configuration.GetSection("SecretManager").Get<SecretManager>();
           // var envPath = Environment.GetEnvironmentVariable(config.SecretManagerPath);
            Env.Load(config.SecretManagerPath);
            var envConfiguration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            
            var keyVaultConfig = new Dictionary<string, string>();
            envConfiguration.GetSection("BlocksSecret").Bind(keyVaultConfig);

            return keyVaultConfig;
        }
    }
}