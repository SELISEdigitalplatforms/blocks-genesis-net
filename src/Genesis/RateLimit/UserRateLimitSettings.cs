using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocks.Genesis;

/// <summary>
/// The per-subject rate limit of this service, resolved once at startup.
/// </summary>
/// <remarks>
/// Resolution order: the <c>UserRateLimitPerSecond</c> environment variable (which includes
/// values loaded from <c>.env</c>), then <see cref="IBlocksSecret.UserRateLimitPerSecond"/>,
/// then <see cref="BlocksConstants.DefaultUserRateLimitPerSecond"/>. A value is valid when it
/// parses as an int greater than 0.
/// </remarks>
public sealed class UserRateLimitSettings
{
    public const string EnvironmentSource = "environment";
    public const string VaultSource = "vault";
    public const string DefaultSource = "default";

    public UserRateLimitSettings(int permitLimit, string source, string serviceName)
    {
        if (permitLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(permitLimit), "The permit limit must be greater than 0.");
        }

        PermitLimit = permitLimit;
        Source = source ?? throw new ArgumentNullException(nameof(source));
        ServiceName = string.IsNullOrWhiteSpace(serviceName) ? "-" : serviceName.Trim();
    }

    /// <summary>Requests allowed per subject per window.</summary>
    public int PermitLimit { get; }

    /// <summary>Where the limit came from: <c>environment</c>, <c>vault</c> or <c>default</c>.</summary>
    public string Source { get; }

    /// <summary>The service name used in the Redis key, so each service keeps its own counts.</summary>
    public string ServiceName { get; }

    /// <summary>Window length in seconds.</summary>
    public int WindowSeconds { get; } = BlocksConstants.RateLimitWindowSeconds;

    /// <summary>
    /// Resolves the limit from the environment, then the vault, then the default, and logs the outcome.
    /// </summary>
    public static UserRateLimitSettings Resolve(
        IBlocksSecret? blocksSecret,
        string serviceName,
        ILogger? logger = null,
        Func<string, string?>? readEnvironment = null)
    {
        logger ??= NullLogger.Instance;
        readEnvironment ??= Environment.GetEnvironmentVariable;

        var settings = ResolveCore(blocksSecret, serviceName, logger, readEnvironment);

        UserRateLimitLog.Resolved(logger, settings.ServiceName, settings.PermitLimit, settings.WindowSeconds, settings.Source);
        return settings;
    }

    private static UserRateLimitSettings ResolveCore(
        IBlocksSecret? blocksSecret,
        string serviceName,
        ILogger logger,
        Func<string, string?> readEnvironment)
    {
        var environmentValue = readEnvironment(BlocksConstants.UserRateLimitEnvironmentVariable);
        if (environmentValue is not null)
        {
            if (int.TryParse(environmentValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromEnvironment) &&
                fromEnvironment > 0)
            {
                return new UserRateLimitSettings(fromEnvironment, EnvironmentSource, serviceName);
            }

            UserRateLimitLog.InvalidSource(logger, EnvironmentSource, BlocksConstants.UserRateLimitEnvironmentVariable);
        }

        var fromVault = blocksSecret?.UserRateLimitPerSecond ?? 0;
        if (fromVault > 0)
        {
            return new UserRateLimitSettings(fromVault, VaultSource, serviceName);
        }

        UserRateLimitLog.InvalidSource(logger, VaultSource, nameof(IBlocksSecret.UserRateLimitPerSecond));

        return new UserRateLimitSettings(BlocksConstants.DefaultUserRateLimitPerSecond, DefaultSource, serviceName);
    }
}

internal static partial class UserRateLimitLog
{
    [LoggerMessage(EventId = 6101, Level = LogLevel.Information,
        Message = "User rate limit resolved. ServiceName={ServiceName} PermitLimit={PermitLimit} WindowSeconds={WindowSeconds} Source={Source}")]
    public static partial void Resolved(ILogger logger, string serviceName, int permitLimit, int windowSeconds, string source);

    [LoggerMessage(EventId = 6102, Level = LogLevel.Warning,
        Message = "User rate limit from {Source} ({Name}) is missing or not a positive integer; skipping it.")]
    public static partial void InvalidSource(ILogger logger, string source, string name);

    [LoggerMessage(EventId = 6103, Level = LogLevel.Warning,
        Message = "User rate limit Redis circuit opened; requests are allowed without limiting until Redis recovers.")]
    public static partial void CircuitOpened(ILogger logger);

    [LoggerMessage(EventId = 6104, Level = LogLevel.Information,
        Message = "User rate limit Redis circuit closed; limiting resumed.")]
    public static partial void CircuitClosed(ILogger logger);
}
