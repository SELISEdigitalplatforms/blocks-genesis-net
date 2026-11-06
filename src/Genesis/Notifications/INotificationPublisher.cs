namespace Blocks.Genesis.Notifications;

/// <summary>Queues notifications for the background notification worker of this service.</summary>
public interface INotificationPublisher
{
    /// <summary>
    /// Queues <paramref name="request"/> and returns at once. Never throws and never waits for I/O: a request without
    /// a name or tenant, with a payload that cannot be serialized or is too big, or that finds the buffer full, is
    /// dropped and logged (name only). Sets <see cref="NotificationRequest.Id"/> when it is empty.
    /// </summary>
    void Publish(NotificationRequest request);
}
