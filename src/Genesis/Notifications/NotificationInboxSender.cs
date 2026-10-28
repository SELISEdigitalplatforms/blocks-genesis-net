using Blocks.Genesis.Entities.Logic;
using Blocks.Genesis.Entities.Os;
using MongoDB.Driver;

namespace Blocks.Genesis.Notifications;

/// <summary>
/// Inbox rows in <c>OfflineNotifications</c>: project users in the tenant DB (one row per user, or one row for the
/// organization at "All users"), console users in the ROOT DB with the project tenant id.
/// </summary>
internal sealed class NotificationInboxSender : INotificationChannelSender
{
    internal const string CollectionName = "OfflineNotifications";
    private const int BatchSize = 1000;

    private readonly IDbContextProvider _dbContextProvider;
    private readonly IBlocksSecret _blocksSecret;
    private readonly TimeProvider _timeProvider;

    public NotificationInboxSender(IDbContextProvider dbContextProvider, IBlocksSecret blocksSecret, TimeProvider timeProvider)
    {
        _dbContextProvider = dbContextProvider;
        _blocksSecret = blocksSecret;
        _timeProvider = timeProvider;
    }

    public string Channel => "inbox";

    public bool IsOn(NotificationChannels channels) => channels.Inbox;

    public async Task SendAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
    {
        var (item, configuration, receivers) = delivery;
        var projectRows = receivers.OrganizationWideId is not null
            ? [Row(item, configuration, userId: null, tenantId: null)]
            : receivers.ProjectUserIds.Select(id => Row(item, configuration, id, tenantId: null)).ToList();
        var consoleRows = receivers.ConsoleUserIds.Select(id => Row(item, configuration, id, item.TenantId)).ToList();

        await InsertAsync(_dbContextProvider.GetDatabase(item.TenantId), projectRows, cancellationToken);
        await InsertAsync(_dbContextProvider.GetDatabase(_blocksSecret.DatabaseConnectionString, _blocksSecret.RootDatabaseName),
            consoleRows, cancellationToken);
    }

    private OfflineNotification Row(QueuedNotification item, NotificationConfiguration configuration, string? userId, string? tenantId) => new()
    {
        Id = Guid.NewGuid().ToString(),
        CorrelationId = item.Id.ToString(),
        CreatedTime = _timeProvider.GetUtcNow().UtcDateTime,
        Payload = new PayloadData
        {
            UserId = userId,
            SubscriptionFilters = item.SubscriptionFilters ?? [],
            NotificationType = configuration.NotificationType.ToString(),
            ResponseKey = item.ResponseKey ?? string.Empty,
            ResponseValue = item.ResponseValue ?? string.Empty,
        },
        // Null is not stored ([BsonIgnoreIfNull]); the property is typed dynamic, not nullable.
        DenormalizedPayload = item.PayloadJson!,
        TenantId = tenantId,
        OrganizationId = item.OrganizationId,
    };

    private static async Task InsertAsync(IMongoDatabase database, List<OfflineNotification> rows, CancellationToken cancellationToken)
    {
        var collection = database.GetCollection<OfflineNotification>(CollectionName);
        foreach (var batch in rows.Chunk(BatchSize))
        {
            await collection.InsertManyAsync(batch, cancellationToken: cancellationToken);
        }
    }
}
