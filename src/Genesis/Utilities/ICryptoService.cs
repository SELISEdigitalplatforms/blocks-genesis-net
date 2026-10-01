namespace Blocks.Genesis;

public interface ICryptoService
{
    string Hash(string value, string? optionalSalt = null, bool makeBase64 = false);
    string Hash(byte[] value, bool makeBase64 = false);
    string ComputeHmacSha256(string message, string key, bool makeBase64 = false);
    bool ConstantTimeEquals(string left, string right);

    /// <summary>
    /// Encrypts <paramref name="plainText"/> with AES-256-GCM under a key derived from
    /// <paramref name="keyMaterial"/>, returning a self-describing base64 envelope.
    /// </summary>
    /// <remarks>
    /// The key is a parameter rather than a property so the key <i>source</i> stays a caller
    /// decision. Moving from a tenant-derived key to a vault-backed one later changes call sites,
    /// not this service.
    /// </remarks>
    string Encrypt(string plainText, string keyMaterial);

    /// <summary>
    /// Reverses <see cref="Encrypt"/>. Returns <c>null</c> when the envelope is malformed, the
    /// version is unknown, or the authentication tag does not verify — the last meaning the value
    /// was tampered with or the key material is wrong.
    /// </summary>
    string? Decrypt(string envelope, string keyMaterial);
}
