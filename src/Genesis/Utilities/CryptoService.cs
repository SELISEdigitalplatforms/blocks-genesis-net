using System.Security.Cryptography;
using System.Text;

namespace Blocks.Genesis
{
    public class CryptoService : ICryptoService
    {
        public string Hash(string value, string salt)
        {
            var sc = BlocksContext.GetContext();
            var valueBytes = Encoding.UTF8.GetBytes(value);

            var saltedValue = valueBytes.Concat(Encoding.UTF8.GetBytes(salt ?? string.Empty)).ToArray();

            return Hash(saltedValue);
        }

        public string Hash(byte[] value, bool makeBase64 = false)
        {
            using (var sha256 = SHA256.Create())
            {
                var hashBytes = sha256.ComputeHash(value);
                return makeBase64 ? Convert.ToBase64String(hashBytes)
                    : BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
