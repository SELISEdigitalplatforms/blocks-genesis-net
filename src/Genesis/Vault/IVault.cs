namespace Blocks.Genesis
{
    public interface IVault
    {
        Task<Dictionary<string, string>> ProcessSecretsAsync(List<string> keys);
    }
}
