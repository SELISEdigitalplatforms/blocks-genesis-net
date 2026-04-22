namespace Blocks.Genesis
{
    public interface ICryptoService
    {
        string Hash(string value, string salt);
        string Hash(byte[] value, bool makeBase64 = false);
        string ComputeHmacSha256(string message, string key, bool makeBase64 = false);
        bool ConstantTimeEquals(string left, string right);
    }
}
