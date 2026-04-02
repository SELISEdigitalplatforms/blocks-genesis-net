namespace Blocks.Genesis
{
    public interface ISecretProvider
    {
        Task<string?> GetAsync(string key);
    }
}
