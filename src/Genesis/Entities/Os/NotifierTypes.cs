// Moved from blocks-os server/Configuration.DomainService/Notification/Enums/NotifierTypes.cs (feature/collection-cut working tree, 2026-10-13).
// Same class, collection and field names; no behaviour change (genesis SPEC1 6.4).
namespace Blocks.Genesis.Entities.Os;

/// <summary>
/// Real-time push channel used to deliver a notification to a connected client.
/// </summary>
public enum NotifierTypes
{
    /// <summary>Push notifications through a SignalR hub connection.</summary>
    SignalR = 0,

    /// <summary>Push notifications through Firebase Cloud Messaging.</summary>
    Firebase = 1,
}

/// <summary>
/// Strategy used to identify which users receive a notification.
/// </summary>
public enum NotificationReceiverTypes
{
    /// <summary>No audience is configured; the notification will not be dispatched.</summary>
    NoReceiverType = 0,

    /// <summary>Notification is broadcast to every connected user.</summary>
    BroadcastReceiverType = 1,

    /// <summary>Notification is sent only to the explicitly listed user IDs.</summary>
    UserSpecificReceiverType = 2,

    /// <summary>Notification is sent to users matching a saved filter expression.</summary>
    FilterSpecificReceiverType = 3,
}
