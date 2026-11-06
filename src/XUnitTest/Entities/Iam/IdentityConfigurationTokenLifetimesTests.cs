using Blocks.Genesis.Entities.Iam;

namespace XUnitTest.Entities.Iam;

public class IdentityConfigurationTokenLifetimesTests
{
    private static IdentityConfiguration Source() => new()
    {
        AccessTokenValidForNumberMinutes = 10,
        RefreshTokenValidForNumberMinutes = 40,
        AbsoluteRefreshTokenValidForNumberMinutes = 500,
        TokenRotationGracePeriodMinutes = 9,
        IsOidcEnabled = true,
    };

    [Fact]
    public void WithTokenLifetimes_AllValues_SetsAllThreeOnTheCopy()
    {
        var copy = Source().WithTokenLifetimes(60, 120, 1440);

        Assert.Equal(60, copy.AccessTokenValidForNumberMinutes);
        Assert.Equal(120, copy.RefreshTokenValidForNumberMinutes);
        Assert.Equal(1440, copy.AbsoluteRefreshTokenValidForNumberMinutes);
    }

    [Fact]
    public void WithTokenLifetimes_NullValues_KeepTheSourceValues()
    {
        var copy = Source().WithTokenLifetimes(null, 120, null);

        Assert.Equal(10, copy.AccessTokenValidForNumberMinutes);
        Assert.Equal(120, copy.RefreshTokenValidForNumberMinutes);
        Assert.Equal(500, copy.AbsoluteRefreshTokenValidForNumberMinutes);
    }

    [Fact]
    public void WithTokenLifetimes_NeverChangesTheSource()
    {
        var source = Source();

        var copy = source.WithTokenLifetimes(60, 120, 1440);

        Assert.NotSame(source, copy);
        Assert.Equal(10, source.AccessTokenValidForNumberMinutes);
        Assert.Equal(40, source.RefreshTokenValidForNumberMinutes);
        Assert.Equal(500, source.AbsoluteRefreshTokenValidForNumberMinutes);
    }

    [Fact]
    public void WithTokenLifetimes_KeepsEveryOtherSetting()
    {
        var copy = Source().WithTokenLifetimes(60, null, null);

        Assert.Equal(9, copy.TokenRotationGracePeriodMinutes);
        Assert.True(copy.IsOidcEnabled);
    }

    [Fact]
    public void AccessTokenDefault_IsUnchanged()
    {
        Assert.Equal(IdentityConfiguration.DefaultAccessTokenValidForNumberMinutes, new IdentityConfiguration().AccessTokenValidForNumberMinutes);
    }
}
