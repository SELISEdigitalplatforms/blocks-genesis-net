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
    AmbiguousHeaderUnmatched = 5
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

        if (string.IsNullOrWhiteSpace(issuer))
        {
            return new ThirdPartyProviderResult(null, ThirdPartyProviderSelection.IssuerUnmatched, 0);
        }

        var byIssuer = providers
            .Where(p => string.Equals(p.Issuer, issuer, StringComparison.Ordinal))
            .ToArray();

        if (byIssuer.Length == 0)
        {
            return new ThirdPartyProviderResult(null, ThirdPartyProviderSelection.IssuerUnmatched, 0);
        }

        var candidates = byIssuer.Where(p => AudienceMatches(p, audiences)).ToArray();

        if (candidates.Length == 0)
        {
            return new ThirdPartyProviderResult(null, ThirdPartyProviderSelection.AudienceUnmatched, byIssuer.Length);
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
