using Blocks.Genesis;
using Xunit;

namespace XUnitTest.Auth;

public class ThirdPartyProviderSelectorTests
{
    private const string Auth0 = "https://dev-kqgrj13jhskombl1.us.auth0.com/";
    private const string Okta = "https://x.okta.com";

    private static ThirdPartyJwtProvider Provider(
        string key,
        string issuer,
        params string[] audiences) => new()
        {
            Key = key,
            Issuer = issuer,
            Audiences = [.. audiences],
            IsActive = true
        };

    [Fact]
    public void SelectsTheOnlyCandidate_WithoutReadingTheHeader()
    {
        var providers = new[] { Provider("auth0-a", Auth0, "api-a"), Provider("okta", Okta, "api-b") };

        var result = ThirdPartyProviderSelector.Select(providers, Auth0, ["api-a"], headerKey: null);

        Assert.Equal(ThirdPartyProviderSelection.Selected, result.Outcome);
        Assert.Equal("auth0-a", result.Provider!.Key);
    }

    [Fact]
    public void SeparatesTwoProvidersSharingAnIssuer_ByAudience()
    {
        // The recommended configuration: same Auth0 tenant, two applications, distinct audiences.
        var providers = new[] { Provider("app-a", Auth0, "api-a"), Provider("app-b", Auth0, "api-b") };

        Assert.Equal("app-a", ThirdPartyProviderSelector.Select(providers, Auth0, ["api-a"], null).Provider!.Key);
        Assert.Equal("app-b", ThirdPartyProviderSelector.Select(providers, Auth0, ["api-b"], null).Provider!.Key);
    }

    [Fact]
    public void FallsBackToTheHeader_WhenIssuerAndAudienceBothMatch()
    {
        var providers = new[] { Provider("app-a", Auth0, "api-a"), Provider("app-b", Auth0, "api-a") };

        var result = ThirdPartyProviderSelector.Select(providers, Auth0, ["api-a"], "app-b");

        Assert.Equal(ThirdPartyProviderSelection.Selected, result.Outcome);
        Assert.Equal("app-b", result.Provider!.Key);
        Assert.Equal(2, result.CandidateCount);
    }

    [Fact]
    public void Rejects_WhenAmbiguousAndNoHeaderSent()
    {
        var providers = new[] { Provider("app-a", Auth0, "api-a"), Provider("app-b", Auth0, "api-a") };

        var result = ThirdPartyProviderSelector.Select(providers, Auth0, ["api-a"], headerKey: null);

        Assert.Equal(ThirdPartyProviderSelection.AmbiguousNoHeader, result.Outcome);
        Assert.Null(result.Provider);
    }

    [Fact]
    public void Rejects_WhenTheHeaderNamesAnUnknownKey()
    {
        var providers = new[] { Provider("app-a", Auth0, "api-a"), Provider("app-b", Auth0, "api-a") };

        var result = ThirdPartyProviderSelector.Select(providers, Auth0, ["api-a"], "app-c");

        Assert.Equal(ThirdPartyProviderSelection.AmbiguousHeaderUnmatched, result.Outcome);
        Assert.Null(result.Provider);
    }

    [Fact]
    public void EmptyAudiences_MatchesAnyAudience()
    {
        // Documents the collapse the design warns about: an empty Audiences list disables audience
        // validation, so this provider matches every token from its issuer.
        var providers = new[] { Provider("wide-open", Auth0) };

        Assert.Equal("wide-open", ThirdPartyProviderSelector.Select(providers, Auth0, ["anything"], null).Provider!.Key);
        Assert.Equal("wide-open", ThirdPartyProviderSelector.Select(providers, Auth0, null, null).Provider!.Key);
    }

    [Fact]
    public void EmptyAudiences_CollapsesTwoProvidersIntoOneCandidateSet()
    {
        // The exact hazard: one provider with no audiences makes an otherwise distinguishable pair
        // ambiguous, handing the claim-mapping choice to the caller's header.
        var providers = new[] { Provider("scoped", Auth0, "api-a"), Provider("wide-open", Auth0) };

        var result = ThirdPartyProviderSelector.Select(providers, Auth0, ["api-a"], headerKey: null);

        Assert.Equal(ThirdPartyProviderSelection.AmbiguousNoHeader, result.Outcome);
        Assert.Equal(2, result.CandidateCount);
    }

    [Fact]
    public void IssuerMatchingIsExactAndOrdinal()
    {
        var providers = new[] { Provider("auth0", Auth0, "api-a") };

        // Auth0 carries a trailing slash; Okta does not. Normalising would match the wrong provider.
        Assert.Equal(
            ThirdPartyProviderSelection.IssuerUnmatched,
            ThirdPartyProviderSelector.Select(providers, Auth0.TrimEnd('/'), ["api-a"], null).Outcome);

        Assert.Equal(
            ThirdPartyProviderSelection.IssuerUnmatched,
            ThirdPartyProviderSelector.Select(providers, Auth0.ToUpperInvariant(), ["api-a"], null).Outcome);
    }

    [Fact]
    public void ReportsIssuerUnmatched_ForABlocksToken()
    {
        // The everyday case on an enabled tenant: a Blocks token, which no provider claims.
        var providers = new[] { Provider("auth0", Auth0, "api-a") };

        var result = ThirdPartyProviderSelector.Select(providers, "SeliseBlocks", ["https://tenant.example"], null);

        Assert.Equal(ThirdPartyProviderSelection.IssuerUnmatched, result.Outcome);
    }

    [Fact]
    public void ReportsAudienceUnmatched_WhenTheIssuerMatchesButTheAudienceDoesNot()
    {
        var providers = new[] { Provider("app-a", Auth0, "api-a") };

        var result = ThirdPartyProviderSelector.Select(providers, Auth0, ["api-z"], null);

        Assert.Equal(ThirdPartyProviderSelection.AudienceUnmatched, result.Outcome);
        Assert.Equal(1, result.CandidateCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReportsIssuerAbsentUnmatched_WhenNoProviderAcceptsIssuerlessTokens(string? issuer)
    {
        // Every configured provider declares an issuer, so a token naming none reaches nothing.
        // Reported apart from IssuerUnmatched because this one is worth a loud log.
        var providers = new[] { Provider("auth0", Auth0, "api-a") };

        Assert.Equal(
            ThirdPartyProviderSelection.IssuerAbsentUnmatched,
            ThirdPartyProviderSelector.Select(providers, issuer, ["api-a"], null).Outcome);
    }

    // ─── issuer-less tokens ────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SelectsTheOnlyIssuerlessProvider_WithoutReadingTheHeader(string? issuer)
    {
        // A provider whose tokens carry no iss. One such provider is unambiguous, so the caller
        // need not send x-blocks-idp -- the same bargain the issuer lane makes.
        var providers = new[] { Provider("recyclium", string.Empty) };

        var result = ThirdPartyProviderSelector.Select(providers, issuer, [], headerKey: null);

        Assert.Equal(ThirdPartyProviderSelection.Selected, result.Outcome);
        Assert.Equal("recyclium", result.Provider!.Key);
    }

    [Fact]
    public void SeparatesTwoIssuerlessProviders_ByHeaderAlone()
    {
        // Nothing in either token distinguishes them, so the header is the only separator. Audience
        // cannot help: a token with no iss carries no aud either.
        var providers = new[] { Provider("partner-a", string.Empty), Provider("partner-b", string.Empty) };

        var result = ThirdPartyProviderSelector.Select(providers, null, [], "partner-b");

        Assert.Equal(ThirdPartyProviderSelection.Selected, result.Outcome);
        Assert.Equal("partner-b", result.Provider!.Key);
        Assert.Equal(2, result.CandidateCount);
    }

    [Fact]
    public void Rejects_TwoIssuerlessProvidersWithNoHeader()
    {
        var providers = new[] { Provider("partner-a", string.Empty), Provider("partner-b", string.Empty) };

        var result = ThirdPartyProviderSelector.Select(providers, null, [], headerKey: null);

        Assert.Equal(ThirdPartyProviderSelection.AmbiguousNoHeader, result.Outcome);
        Assert.Null(result.Provider);
    }

    [Fact]
    public void ABlankIssuerIsNotAWildcard()
    {
        // The rule the whole design rests on. Were a blank issuer to match anything, this provider
        // would be offered every token from every issuer -- and for an HMAC provider that means its
        // shared secret gets tried against tokens it was never meant to see.
        var providers = new[] { Provider("catch-all", string.Empty) };

        var result = ThirdPartyProviderSelector.Select(providers, Auth0, ["api-a"], headerKey: null);

        Assert.Equal(ThirdPartyProviderSelection.IssuerUnmatched, result.Outcome);
        Assert.Null(result.Provider);
    }

    [Fact]
    public void ABlankIssuerIsNotAWildcard_EvenWhenTheHeaderNamesIt()
    {
        // The header is a tiebreaker among candidates, never a way to reach a provider the token
        // does not qualify for. Otherwise a caller could route any token anywhere.
        var providers = new[] { Provider("catch-all", string.Empty) };

        var result = ThirdPartyProviderSelector.Select(providers, Auth0, ["api-a"], "catch-all");

        Assert.Equal(ThirdPartyProviderSelection.IssuerUnmatched, result.Outcome);
        Assert.Null(result.Provider);
    }

    [Fact]
    public void AnIssuerBearingTokenNeverFallsThroughToAnIssuerlessProvider()
    {
        // Mixed project: Auth0 alongside a partner whose tokens carry no iss. A token from a third,
        // unknown issuer must fail closed rather than land on the partner's provider.
        var providers = new[] { Provider("auth0", Auth0, "api-a"), Provider("partner", string.Empty) };

        var result = ThirdPartyProviderSelector.Select(providers, "https://unknown.example/", ["api-a"], null);

        Assert.Equal(ThirdPartyProviderSelection.IssuerUnmatched, result.Outcome);
        Assert.Null(result.Provider);
    }

    [Fact]
    public void TheTwoLanesDoNotCompete()
    {
        // Each token reaches exactly the provider configured for its shape, and neither provider
        // can capture the other's tokens.
        var providers = new[] { Provider("auth0", Auth0, "api-a"), Provider("partner", string.Empty) };

        Assert.Equal("auth0", ThirdPartyProviderSelector.Select(providers, Auth0, ["api-a"], null).Provider!.Key);
        Assert.Equal("partner", ThirdPartyProviderSelector.Select(providers, null, [], null).Provider!.Key);
    }

    [Fact]
    public void AudienceStaysStrict_InTheIssuerlessLaneToo()
    {
        // A provider demanding an audience refuses a token that carries none, whichever lane it
        // arrived in. Relaxing this for issuer-less tokens would quietly disable the one check
        // such a provider still has: nothing else about the token is being validated for identity.
        var providers = new[] { Provider("partner", string.Empty, "api-a") };

        var result = ThirdPartyProviderSelector.Select(providers, null, [], headerKey: null);

        Assert.Equal(ThirdPartyProviderSelection.AudienceUnmatched, result.Outcome);
        Assert.Null(result.Provider);
    }

    [Fact]
    public void AnIssuerlessTokenCarryingAnAudience_MatchesOnIt()
    {
        // A token may well carry aud but no iss, and then the audience is genuinely useful --
        // which is why audiences are allowed on an issuer-less provider rather than forbidden.
        var providers = new[] { Provider("partner", string.Empty, "api-a") };

        var result = ThirdPartyProviderSelector.Select(providers, null, ["api-a"], headerKey: null);

        Assert.Equal(ThirdPartyProviderSelection.Selected, result.Outcome);
        Assert.Equal("partner", result.Provider!.Key);
    }

    [Fact]
    public void AudiencesSeparateTwoIssuerlessProviders_WithoutAHeader()
    {
        // So the header is only unavoidable when the tokens really are indistinguishable.
        var providers = new[]
        {
            Provider("partner-a", string.Empty, "api-a"),
            Provider("partner-b", string.Empty, "api-b")
        };

        Assert.Equal("partner-a", ThirdPartyProviderSelector.Select(providers, null, ["api-a"], null).Provider!.Key);
        Assert.Equal("partner-b", ThirdPartyProviderSelector.Select(providers, null, ["api-b"], null).Provider!.Key);
    }

    [Fact]
    public void ReportsNoProviders_ForAnEmptyOrNullList()
    {
        Assert.Equal(ThirdPartyProviderSelection.NoProviders, ThirdPartyProviderSelector.Select([], Auth0, ["api-a"], null).Outcome);
        Assert.Equal(ThirdPartyProviderSelection.NoProviders, ThirdPartyProviderSelector.Select(null, Auth0, ["api-a"], null).Outcome);
    }
}
