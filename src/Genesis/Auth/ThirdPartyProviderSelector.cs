namespace Blocks.Genesis;

/// <summary>Why provider selection ended the way it did. Drives the log, not the control flow.</summary>
public enum ThirdPartyProviderSelection
{
    Selected = 0,

    /// <summary>The tenant has no active providers at all.</summary>
    NoProviders = 1,

    /// <summary>No provider is configured for the token's issuer. Usually a Blocks token, or a typo.</summary>
    IssuerUnmatched = 2,

    /// <summary>The token's audience matched no provider for that issuer.</summary>
    AudienceUnmatched = 3,

    /// <summary>Several providers are indistinguishable and no <c>x-blocks-idp</c> header was sent.</summary>
    AmbiguousNoHeader = 4,

    /// <summary>The header named a key that is not among the candidates.</summary>
    AmbiguousHeaderUnmatched = 5,

    /// <summary>
    /// The token carries no <c>iss</c> at all, and no provider is configured to receive such
    /// tokens.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="IssuerUnmatched"/> because the two deserve opposite treatment in
    /// a log. An unrecognised issuer is the everyday case -- every Blocks token looks like that --
    /// so it stays quiet. A token with no issuer whatsoever is not something this platform mints,
    /// so it is worth reporting loudly rather than losing in the noise.
    /// </remarks>
    IssuerAbsentUnmatched = 6
}

public readonly record struct ThirdPartyProviderResult(
    ThirdPartyJwtProvider? Provider,
    ThirdPartyProviderSelection Outcome,
    int CandidateCount)
{
    public bool IsSelected => Provider is not null;
}

/// <summary>
/// Picks which configured provider a token belongs to.
/// </summary>
/// <remarks>
/// <para>
/// Narrows cryptographically first and asks the client only when that cannot separate two
/// candidates. Reading <c>iss</c> and <c>aud</c> from an unvalidated token is safe here because
/// they only select a key set — validation then re-checks both against the chosen provider, so a
/// forged issuer routes to a configuration whose keys will not verify the signature.
/// </para>
/// <para>
/// Matching is exact and ordinal. Auth0's issuer carries a trailing slash and Okta's does not;
/// normalising would invite matching the wrong provider.
/// </para>
/// <para>
/// <b>Whether the token carries an <c>iss</c> decides which set of providers it can reach at
/// all, and the two sets are disjoint.</b> A token with an issuer can only reach providers that
/// declare that exact issuer; a token without one can only reach providers that declare no
/// issuer. So a blank issuer is <b>not a wildcard</b> — it does not widen a provider to accept
/// everything, it narrows it to the tokens that name nobody.
/// </para>
/// <para>
/// That is deliberately the opposite of how an empty audience list behaves, and the asymmetry is
/// the point. Audience is a filter applied <i>within</i> an already-identified sender, so an empty
/// one means "do not filter". Issuer <i>is</i> the sender's identity, and widening that would make
/// one provider a catch-all offered every token from every other issuer — for an HMAC provider,
/// "offered" means its shared secret gets tried against tokens it was never meant to see.
/// </para>
/// </remarks>
public static class ThirdPartyProviderSelector
{
    public static ThirdPartyProviderResult Select(
        IReadOnlyList<ThirdPartyJwtProvider>? providers,
        string? issuer,
        IReadOnlyCollection<string>? audiences,
        string? headerKey)
    {
        if (providers is null || providers.Count == 0)
        {
            return new ThirdPartyProviderResult(null, ThirdPartyProviderSelection.NoProviders, 0);
        }

        // A token that names no issuer can only reach a provider that declares none. Not a
        // wildcard match against every provider -- see the asymmetry note on this class.
        if (string.IsNullOrWhiteSpace(issuer))
        {
            var issuerless = providers
                .Where(p => string.IsNullOrWhiteSpace(p.Issuer))
                .ToArray();

            return issuerless.Length == 0
                ? new ThirdPartyProviderResult(null, ThirdPartyProviderSelection.IssuerAbsentUnmatched, 0)
                : Narrow(issuerless, audiences, headerKey);
        }

        var byIssuer = providers
            .Where(p => string.Equals(p.Issuer, issuer, StringComparison.Ordinal))
            .ToArray();

        // Deliberately no fallback to the issuer-less providers. A token whose issuer nothing
        // claims fails closed rather than being handed to whichever provider left the field blank.
        if (byIssuer.Length == 0)
        {
            return new ThirdPartyProviderResult(null, ThirdPartyProviderSelection.IssuerUnmatched, 0);
        }

        return Narrow(byIssuer, audiences, headerKey);
    }

    /// <summary>
    /// Reduces an already issuer-matched set to one provider, by audience and then by header.
    /// </summary>
    /// <remarks>
    /// Shared by both lanes so they cannot drift. An issuer-less set reaches here with a token
    /// that has no audience either, which every provider's audience rule accepts — so for that
    /// lane this collapses to "one candidate selects itself, several need the header", which is
    /// the same bargain the issuer lane makes.
    /// </remarks>
    private static ThirdPartyProviderResult Narrow(
        ThirdPartyJwtProvider[] matched,
        IReadOnlyCollection<string>? audiences,
        string? headerKey)
    {
        var candidates = matched.Where(p => AudienceMatches(p, audiences)).ToArray();

        if (candidates.Length == 0)
        {
            return new ThirdPartyProviderResult(null, ThirdPartyProviderSelection.AudienceUnmatched, matched.Length);
        }

        if (candidates.Length == 1)
        {
            // The common case: issuer and audience already identify one provider, so the header is
            // never read and callers need not send it.
            return new ThirdPartyProviderResult(candidates[0], ThirdPartyProviderSelection.Selected, 1);
        }

        // Several providers share both issuer and audience, so nothing cryptographic tells their
        // tokens apart and the header alone decides which claim mapping applies. Those providers
        // must carry equivalent privilege — enforced where they are saved, not here.
        if (string.IsNullOrWhiteSpace(headerKey))
        {
            return new ThirdPartyProviderResult(null, ThirdPartyProviderSelection.AmbiguousNoHeader, candidates.Length);
        }

        var named = candidates.FirstOrDefault(p => string.Equals(p.Key, headerKey, StringComparison.Ordinal));

        return named is null
            ? new ThirdPartyProviderResult(null, ThirdPartyProviderSelection.AmbiguousHeaderUnmatched, candidates.Length)
            : new ThirdPartyProviderResult(named, ThirdPartyProviderSelection.Selected, candidates.Length);
    }

    // An empty Audiences list disables audience validation for that provider, so it matches any
    // token from its issuer. That is also what collapses several providers into one candidate set.
    private static bool AudienceMatches(ThirdPartyJwtProvider provider, IReadOnlyCollection<string>? audiences)
    {
        if (provider.Audiences is null || provider.Audiences.Count == 0)
        {
            return true;
        }

        return audiences is not null
            && audiences.Any(a => provider.Audiences.Contains(a, StringComparer.Ordinal));
    }
}
