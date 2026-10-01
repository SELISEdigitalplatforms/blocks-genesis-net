using Microsoft.IdentityModel.Tokens;

namespace Blocks.Genesis;

/// <summary>
/// Signing algorithm a third-party identity provider uses, as configured rather than as claimed.
/// </summary>
/// <remarks>
/// <para>
/// The only other source for this is the token's own <c>alg</c> header, which is attacker
/// controlled. Selecting a key source from it is the algorithm-confusion setup: take the RSA
/// public key, use its bytes as an HMAC secret, sign <c>HS256</c>. Configuration decides; the
/// token never does.
/// </para>
/// <para>
/// Persisted as an int, so these values are part of the stored contract: append new members,
/// never renumber existing ones.
/// </para>
/// </remarks>
public enum JwtSigningAlgorithm
{
    /// <summary>
    /// Rows written before this field existed. For a field that selects a key source, unset must
    /// be rejected rather than defaulted to something plausible.
    /// </summary>
    Unspecified = 0,

    RS256 = 1,
    RS384 = 2,
    RS512 = 3,

    ES256 = 4,
    ES384 = 5,
    ES512 = 6,

    PS256 = 7,
    PS384 = 8,
    PS512 = 9,

    HS256 = 10,
    HS384 = 11,
    HS512 = 12
}

/// <summary>
/// Maps <see cref="JwtSigningAlgorithm"/> onto the two things it decides: which <c>alg</c> values
/// validation will accept, and where the key comes from.
/// </summary>
public static class JwtSigningAlgorithms
{
    // The wire names come from SecurityAlgorithms rather than Enum.ToString(). The enum member
    // names happen to match today; tying the stored contract to the validator's constants says
    // that is intentional rather than coincidental.
    private static readonly Dictionary<JwtSigningAlgorithm, string> WireNames = new()
    {
        [JwtSigningAlgorithm.RS256] = SecurityAlgorithms.RsaSha256,
        [JwtSigningAlgorithm.RS384] = SecurityAlgorithms.RsaSha384,
        [JwtSigningAlgorithm.RS512] = SecurityAlgorithms.RsaSha512,
        [JwtSigningAlgorithm.ES256] = SecurityAlgorithms.EcdsaSha256,
        [JwtSigningAlgorithm.ES384] = SecurityAlgorithms.EcdsaSha384,
        [JwtSigningAlgorithm.ES512] = SecurityAlgorithms.EcdsaSha512,
        [JwtSigningAlgorithm.PS256] = SecurityAlgorithms.RsaSsaPssSha256,
        [JwtSigningAlgorithm.PS384] = SecurityAlgorithms.RsaSsaPssSha384,
        [JwtSigningAlgorithm.PS512] = SecurityAlgorithms.RsaSsaPssSha512,
        [JwtSigningAlgorithm.HS256] = SecurityAlgorithms.HmacSha256,
        [JwtSigningAlgorithm.HS384] = SecurityAlgorithms.HmacSha384,
        [JwtSigningAlgorithm.HS512] = SecurityAlgorithms.HmacSha512
    };

    /// <summary>True for the HMAC family, whose key is a shared secret rather than a published one.</summary>
    public static bool IsSymmetric(this JwtSigningAlgorithm algorithm) =>
        algorithm is JwtSigningAlgorithm.HS256 or JwtSigningAlgorithm.HS384 or JwtSigningAlgorithm.HS512;

    /// <summary>The <c>alg</c> header value, or empty for <see cref="JwtSigningAlgorithm.Unspecified"/>.</summary>
    public static string ToWireName(this JwtSigningAlgorithm algorithm) =>
        WireNames.TryGetValue(algorithm, out var name) ? name : string.Empty;

    /// <summary>
    /// The <c>alg</c> values validation will accept, for pinning <c>ValidAlgorithms</c>.
    /// Unspecified members contribute nothing, so a misconfigured row yields an empty set and is
    /// rejected rather than silently widening to whatever the key type happens to support.
    /// </summary>
    public static string[] ToWireNames(this IEnumerable<JwtSigningAlgorithm>? algorithms) =>
        algorithms is null
            ? []
            : algorithms.Select(ToWireName)
                        .Where(name => !string.IsNullOrEmpty(name))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();

    /// <summary>
    /// Whether every configured algorithm draws its key from the same place. A provider mixing
    /// families would need both a JWKS and a secret, which is two independent signing authorities
    /// for one provider.
    /// </summary>
    public static bool IsSingleKeySource(this IReadOnlyCollection<JwtSigningAlgorithm>? algorithms)
    {
        if (algorithms is null || algorithms.Count == 0)
        {
            return false;
        }

        if (algorithms.Any(a => a == JwtSigningAlgorithm.Unspecified))
        {
            return false;
        }

        return algorithms.All(a => a.IsSymmetric()) || algorithms.All(a => !a.IsSymmetric());
    }
}
