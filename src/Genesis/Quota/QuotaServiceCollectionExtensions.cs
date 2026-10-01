using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Blocks.Genesis;

public static class QuotaServiceCollectionExtensions
{
    /// <summary>
    /// Registers the quota client. One registration line per service; everything else is the
    /// service calling <see cref="IQuota.ConsumeAsync"/> before it does the work.
    /// </summary>
    public static IServiceCollection AddBlocksQuota(this IServiceCollection services, Action<QuotaOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new QuotaOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<QuotaFlushTracker>();
        services.TryAddSingleton<IQuotaLimitStore, MongoQuotaLimitStore>();
        services.TryAddSingleton<IQuota, RedisQuota>();
        services.AddHostedService<QuotaFlushService>();

        return services;
    }
}
