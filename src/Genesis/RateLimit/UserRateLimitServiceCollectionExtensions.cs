using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Blocks.Genesis;

/// <summary>
/// Registers the per-subject rate limiter. Called by <see cref="ApplicationConfigurations.ConfigureServices"/>;
/// consumers do not call it.
/// </summary>
internal static class UserRateLimitServiceCollectionExtensions
{
    internal const string LoggerCategory = "Blocks.Genesis.UserRateLimit";

    internal static IServiceCollection AddUserRateLimiting(
        this IServiceCollection services,
        Func<IServiceProvider, string> resolveServiceName,
        Func<string, string?>? readEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(resolveServiceName);

        services.TryAddSingleton(sp => UserRateLimitSettings.Resolve(
            sp.GetService<IBlocksSecret>(),
            resolveServiceName(sp),
            sp.GetService<ILoggerFactory>()?.CreateLogger(LoggerCategory),
            readEnvironment));

        services.TryAddSingleton<IUserRateLimiter>(sp => new RedisSlidingWindowRateLimiter(
            sp.GetRequiredService<ICacheClient>(),
            sp.GetRequiredService<UserRateLimitSettings>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger(LoggerCategory),
            RedisRateLimitResilienceOptions.Default));

        return services;
    }
}
