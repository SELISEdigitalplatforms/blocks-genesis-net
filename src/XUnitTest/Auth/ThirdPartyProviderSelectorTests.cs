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
    public void ReportsIssuerUnmatched_WhenTheTokenHasNoIssuer(string? issuer)
    {
        var providers = new[] { Provider("auth0", Auth0, "api-a") };

        Assert.Equal(
            ThirdPartyProviderSelection.IssuerUnmatched,
            ThirdPartyProviderSelector.Select(providers, issuer, ["api-a"], null).Outcome);
    }

    [Fact]
    public void ReportsNoProviders_ForAnEmptyOrNullList()
    {
        Assert.Equal(ThirdPartyProviderSelection.NoProviders, ThirdPartyProviderSelector.Select([], Auth0, ["api-a"], null).Outcome);
        Assert.Equal(ThirdPartyProviderSelection.NoProviders, ThirdPartyProviderSelector.Select(null, Auth0, ["api-a"], null).Outcome);
    }
}
