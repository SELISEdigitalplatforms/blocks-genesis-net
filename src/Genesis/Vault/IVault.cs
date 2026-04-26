using Microsoft.Extensions.Configuration;

namespace Blocks.Genesis
{
    public interface IVault
    {
        Task<Dictionary<string, string>> ProcessSecretsAsync(List<string> keys, IConfiguration configuration);
    }
}
