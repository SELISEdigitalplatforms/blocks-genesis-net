using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Blocks.Genesis.Notifications;

public static class NotificationServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="INotificationPublisher"/>, the background worker and the SignalR client over the Redis
    /// backplane (genesis cache connection). Needs the usual genesis services (ConfigureServices or
    /// ConfigureWorker): IBlocksSecret, ICacheClient, ITenants, IDbContextProvider, IKeyValueStore, IMessageClient.
    /// Mapping <see cref="NotificationHub"/> is not needed to send; the service that maps it also registers an
    /// <see cref="INotificationHubHandler"/>.
    /// </summary>
    public static IServiceCollection AddBlocksNotifications(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<NotificationQueue>();
        services.TryAddSingleton<INotificationPublisher, NotificationPublisher>();
        services.TryAddSingleton<INotificationConfigReader, NotificationConfigReader>();
        services.TryAddSingleton<INotificationRateLimiter, RedisNotificationRateLimiter>();
        services.TryAddSingleton<INotificationDirectory, MongoNotificationDirectory>();
        services.TryAddSingleton<INotificationReceiverResolver, NotificationReceiverResolver>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationChannelSender, NotificationInboxSender>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationChannelSender, NotificationLiveSender>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationChannelSender, NotificationPushSender>());
        services.TryAddSingleton<INotificationDispatcher, NotificationDispatcher>();
        services.AddHostedService<NotificationWorker>();

        // Same backplane settings as logic's AddStackExchangeRedis(CacheConnectionString): no channel prefix, so a
        // send from any service reaches the clients connected to logic's hub.
        services.AddSignalR().AddStackExchangeRedis();
        services.AddOptions<RedisOptions>().Configure<IBlocksSecret>((options, secret) =>
        {
            if (!string.IsNullOrWhiteSpace(secret.CacheConnectionString))
            {
                options.Configuration = ConfigurationOptions.Parse(secret.CacheConnectionString);
            }
        });

        return services;
    }
}
