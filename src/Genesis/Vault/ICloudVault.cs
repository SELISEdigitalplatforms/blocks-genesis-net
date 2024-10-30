namespace Blocks.Genesis
{
    public interface ICloudVault
    {
        Task<Dictionary<string, string>> ProcessSecretsAsync(List<string> keys, Dictionary<string, string> cloudConfig);
        Task<string> ProcessSecretAsync(string key, Dictionary<string, string> cloudConfig);
        Task<byte[]> ProcessCertificateAsync(string certificateName, Dictionary<string, string> cloudConfig);
    }
}
