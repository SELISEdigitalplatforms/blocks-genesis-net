using System.Security.Cryptography;
using System.Text;

namespace Blocks.Genesis;

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

    // Envelope: version | nonce | ciphertext | tag, base64.
    //
    // The version byte is the escape hatch. Changing cipher or key derivation later — including
    // moving to a vault-backed key — becomes a decode-time branch rather than a migration
    // everyone has to coordinate.
    private const byte EnvelopeV1 = 1;
    private const int NonceBytes = 12;   // AesGcm.NonceByteSizes maximum, and the GCM standard
    private const int TagBytes = 16;     // AesGcm.TagByteSizes maximum

    /// <inheritdoc />
    public string Encrypt(string plainText, string keyMaterial)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyMaterial);

        var key = DeriveKey(keyMaterial);
        var plainBytes = Encoding.UTF8.GetBytes(plainText);

        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagBytes];

        using (var aes = new AesGcm(key, TagBytes))
        {
            aes.Encrypt(nonce, plainBytes, cipherBytes, tag);
        }

        var envelope = new byte[1 + NonceBytes + cipherBytes.Length + TagBytes];
        envelope[0] = EnvelopeV1;
        nonce.CopyTo(envelope, 1);
        cipherBytes.CopyTo(envelope, 1 + NonceBytes);
        tag.CopyTo(envelope, 1 + NonceBytes + cipherBytes.Length);

        return Convert.ToBase64String(envelope);
    }

    /// <inheritdoc />
    public string? Decrypt(string envelope, string keyMaterial)
    {
        if (string.IsNullOrWhiteSpace(envelope) || string.IsNullOrWhiteSpace(keyMaterial))
        {
            return null;
        }

        byte[] raw;

        try
        {
            raw = Convert.FromBase64String(envelope);
        }
        catch (FormatException)
        {
            return null;
        }

        // A short buffer is malformed input, not a cryptographic failure; check before slicing.
        if (raw.Length < 1 + NonceBytes + TagBytes || raw[0] != EnvelopeV1)
        {
            return null;
        }

        var cipherLength = raw.Length - 1 - NonceBytes - TagBytes;
        var nonce = raw.AsSpan(1, NonceBytes);
        var cipherBytes = raw.AsSpan(1 + NonceBytes, cipherLength);
        var tag = raw.AsSpan(1 + NonceBytes + cipherLength, TagBytes);
        var plainBytes = new byte[cipherLength];

        try
        {
            using var aes = new AesGcm(DeriveKey(keyMaterial), TagBytes);
            aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
        }
        catch (CryptographicException)
        {
            // The tag did not verify: the value was tampered with, or the key material is wrong.
            // Both are the caller's to report — this layer only says "no".
            return null;
        }

        return Encoding.UTF8.GetString(plainBytes);
    }

    // Derived rather than used raw, so the stored key material and the AES key are never the same
    // bytes, and any length of input yields a valid AES-256 key.
    private static byte[] DeriveKey(string keyMaterial) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial));

}
