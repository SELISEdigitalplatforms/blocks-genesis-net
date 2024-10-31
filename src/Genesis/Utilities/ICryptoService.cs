namespace Blocks.Genesis
{
    public interface ICryptoService
    {
        string Hash(string value, string salt);
        string Hash(byte[] value, bool makeBase64 = false);
    }
}
