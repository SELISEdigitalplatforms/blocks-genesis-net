namespace Blocks.Genesis
{
    public interface ICloudVault
    {
        Task<BlocksSecret> ProcessSecrets(BlocksSecret globalConfig, Dictionary<string, string> cloudConfig);
    }
}
