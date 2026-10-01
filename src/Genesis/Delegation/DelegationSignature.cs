using System.Security.Cryptography;
using System.Text;

namespace Blocks.Genesis;

/// <summary>
/// The signature scheme protecting a token exchange. Kept separate from the provider because it is
/// a cross-SDK contract: blocks-genesis-py and blocks-iam must produce and verify the same bytes.
/// </summary>
public static class DelegationSignature
{
    /// <summary>
    /// HMAC-SHA256 over the pipe-delimited input, keyed by the tenant salt (UTF-8 bytes).
    /// Returned as lowercase hex.
    /// </summary>
    public static string Compute(string signatureInput, string tenantSalt)
    {
        ArgumentNullException.ThrowIfNull(signatureInput);
        ArgumentNullException.ThrowIfNull(tenantSalt);

        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(tenantSalt), Encoding.UTF8.GetBytes(signatureInput));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    /// <summary>Convenience overload building the input from its parts.</summary>
    public static string Compute(string tenantId, string delegationId, string nonce, long ts, string tenantSalt)
        => Compute(DelegationConstants.BuildSignatureInput(tenantId, delegationId, nonce, ts), tenantSalt);

    /// <summary>A single-use exchange nonce: 16 cryptographically random bytes, lowercase hex.</summary>
    public static string NewNonce()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(DelegationConstants.NonceRandomBytes)).ToLowerInvariant();

    /// <summary>Constant-time comparison of two hex signatures.</summary>
    public static bool Verify(string expected, string? presented)
    {
        if (string.IsNullOrEmpty(presented) || expected.Length != presented.Length) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(presented));
    }
}
