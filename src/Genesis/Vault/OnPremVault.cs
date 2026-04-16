using DotNetEnv;
using Microsoft.Extensions.Configuration;

namespace Blocks.Genesis
{
    public class OnPremVault : IVault
    {
        public async Task<Dictionary<string, string>> ProcessSecretsAsync(List<string> keys)
        {
            return await Task.FromResult(GetVaultValues());
        }

        public static Dictionary<string, string> GetVaultValues()
        {
            var envPath = Environment.GetEnvironmentVariable("envPath");
            Env.Load(envPath);

            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            var keyVaultConfig = new Dictionary<string, string>();
            configuration.GetSection("BlocksSecret").Bind(keyVaultConfig);

            return keyVaultConfig;
        }
    }
}