namespace Blocks.Genesis
{
    public interface ICloudVault
    {
        Task<Dictionary<string, string>> ProcessSecrets(List<string> keys, Dictionary<string, string> cloudConfig);
        Task<string> ProcessSecret(string key, Dictionary<string, string> cloudConfig);
    }
}
