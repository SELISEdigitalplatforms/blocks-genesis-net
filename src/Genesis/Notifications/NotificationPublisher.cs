using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Blocks.Genesis.Notifications;

/// <summary>Validates and queues a request; never throws and never awaits I/O.</summary>
internal sealed class NotificationPublisher : INotificationPublisher
{
    internal const int MaxPayloadChars = 256 * 1024;

    private readonly NotificationQueue _queue;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NotificationPublisher> _logger;

    public NotificationPublisher(NotificationQueue queue, TimeProvider timeProvider, ILogger<NotificationPublisher> logger)
    {
        _queue = queue;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public void Publish(NotificationRequest request)
    {
        try
        {
            var item = ToQueued(request);
            if (item is not null)
            {
                _queue.Enqueue(item);
            }
        }
        catch (Exception ex)
        {
            // Type only: the message of a serializer error can hold payload values.
            _logger.LogWarning("Notification {Name} not queued: {Error}", request?.Name, ex.GetType().Name); // NOSONAR S6667 — type only
        }
    }

    private QueuedNotification? ToQueued(NotificationRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            _logger.LogWarning("Notification without a name not queued");
            return null;
        }

        var context = BlocksContext.GetContext();
        var tenantId = string.IsNullOrWhiteSpace(request.TenantId) ? context?.TenantId : request.TenantId.Trim();
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            _logger.LogWarning("Notification {Name} not queued: no tenant", request.Name);
            return null;
        }

        var payloadJson = SerializePayload(request.Payload);
        if (payloadJson?.Length > MaxPayloadChars)
        {
            _logger.LogWarning("Notification {Name} not queued: payload over {Max} characters", request.Name, MaxPayloadChars);
            return null;
        }

        if (request.Id == Guid.Empty)
        {
            request.Id = Guid.NewGuid();
        }

        return new QueuedNotification
        {
            Id = request.Id,
            Name = request.Name.Trim(),
            TenantId = tenantId,
            OrganizationId = SendOrganization.Resolve(context, tenantId, request.OrganizationId),
            NamedUserIds = CleanIds(request.NamedUserIds),
            PayloadJson = payloadJson,
            SubscriptionFilters = request.SubscriptionFilters?.Where(f => f is not null).ToList(),
            ResponseKey = request.ResponseKey,
            ResponseValue = request.ResponseValue,
            QueuedAt = _timeProvider.GetUtcNow().UtcDateTime,
            Sender = context is null
                ? NotificationSender.None
                : new NotificationSender(context.UserId, context.OriginalTenantId, context.Impersonated),
        };
    }

    private static string? SerializePayload(object? payload) => payload switch
    {
        null => null,
        string json => json,
        _ => JsonSerializer.Serialize(payload),
    };

    private static string[] CleanIds(IReadOnlyList<string>? ids) =>
        ids is null
            ? []
            : ids.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.Ordinal).ToArray();
}
