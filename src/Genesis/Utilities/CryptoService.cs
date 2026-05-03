using System.Security.Cryptography;
using System.Text;

namespace Blocks.Genesis
{
    public class CryptoService : ICryptoService
    {
        public string Hash(string value, string? optionalSalt = null, bool makeBase64 = false)
        {
            var saltedValue = value + (optionalSalt ?? string.Empty);
            var valueBytes = Encoding.UTF8.GetBytes(saltedValue);
            return Hash(valueBytes, makeBase64);
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

        public string ComputeHmacSha256(string message, string key, bool makeBase64 = false)
        {
            var safeMessage = message ?? string.Empty;
            var safeKey = key ?? string.Empty;

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(safeKey));
            var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(safeMessage));

            return makeBase64
                ? Convert.ToBase64String(hashBytes)
                : Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        public bool ConstantTimeEquals(string left, string right)
        {
            var leftBytes = Encoding.UTF8.GetBytes(left ?? string.Empty);
            var rightBytes = Encoding.UTF8.GetBytes(right ?? string.Empty);
            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
    }
}
