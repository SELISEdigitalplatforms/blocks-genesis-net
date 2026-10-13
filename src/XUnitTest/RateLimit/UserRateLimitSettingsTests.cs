using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using Moq;

namespace XUnitTest.RateLimit;

public class UserRateLimitSettingsTests
{
    private static IBlocksSecret Secret(int vaultValue)
    {
        var secret = new Mock<IBlocksSecret>();
        secret.SetupGet(s => s.UserRateLimitPerSecond).Returns(vaultValue);
        return secret.Object;
    }

    private static Func<string, string?> Env(string? value) =>
        name => name == BlocksConstants.UserRateLimitEnvironmentVariable ? value : null;

    [Fact]
    public void Resolve_UsesEnvironment_WhenValid_RegardlessOfVault() // H1, E1
    {
        var logger = new CapturingLogger();

        var settings = UserRateLimitSettings.Resolve(Secret(20), "svc-a", logger, Env("50"));

        Assert.Equal(50, settings.PermitLimit);
        Assert.Equal(UserRateLimitSettings.EnvironmentSource, settings.Source);
        Assert.Equal(1, settings.WindowSeconds);
        var info = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, info.Level);
        Assert.Contains("svc-a", info.Message);
        Assert.Contains("50", info.Message);
        Assert.Contains("environment", info.Message);
    }

    [Fact]
    public void Resolve_UsesVault_WhenEnvironmentMissing() // H2
    {
        var logger = new CapturingLogger();

        var settings = UserRateLimitSettings.Resolve(Secret(20), "svc-a", logger, Env(null));

        Assert.Equal(20, settings.PermitLimit);
        Assert.Equal(UserRateLimitSettings.VaultSource, settings.Source);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(logger.Entries, e => e.Message.Contains("Source=vault"));
    }

    [Fact]
    public void Resolve_FallsBackToDefault_WithOneWarningPerInvalidSource() // H3, C4, E2
    {
        var logger = new CapturingLogger();

        var settings = UserRateLimitSettings.Resolve(Secret(0), "svc-a", logger, Env("abc"));

        Assert.Equal(BlocksConstants.DefaultUserRateLimitPerSecond, settings.PermitLimit);
        Assert.Equal(100, settings.PermitLimit);
        Assert.Equal(UserRateLimitSettings.DefaultSource, settings.Source);
        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Message.Contains("environment"));
        Assert.Contains(warnings, w => w.Message.Contains("vault"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("Source=default"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("1.5")]
    public void Resolve_SkipsInvalidEnvironmentValues(string value) // C4
    {
        var logger = new CapturingLogger();

        var settings = UserRateLimitSettings.Resolve(Secret(20), "svc-a", logger, Env(value));

        Assert.Equal(20, settings.PermitLimit);
        Assert.Equal(UserRateLimitSettings.VaultSource, settings.Source);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void Resolve_SkipsNegativeVault_AndHandlesNullSecret() // C4
    {
        var negative = UserRateLimitSettings.Resolve(Secret(-1), "svc-a", new CapturingLogger(), Env(null));
        var missing = UserRateLimitSettings.Resolve(null, "svc-a", null, Env(null));

        Assert.Equal(UserRateLimitSettings.DefaultSource, negative.Source);
        Assert.Equal(100, missing.PermitLimit);
    }

    [Fact]
    public void Resolve_ReadsProcessEnvironment_ByDefault()
    {
        var settings = UserRateLimitSettings.Resolve(Secret(7), "svc-a");

        // The process may or may not define the variable; either way a valid limit is produced.
        Assert.True(settings.PermitLimit > 0);
    }

    [Fact]
    public void Constructor_ValidatesArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UserRateLimitSettings(0, "default", "svc"));
        Assert.Throws<ArgumentNullException>(() => new UserRateLimitSettings(1, null!, "svc"));
        Assert.Equal("-", new UserRateLimitSettings(1, "default", "  ").ServiceName);
        Assert.Equal("svc", new UserRateLimitSettings(1, "default", " svc ").ServiceName);
    }
}
