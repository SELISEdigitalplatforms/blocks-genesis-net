using Microsoft.AspNetCore.SignalR;

namespace Blocks.Genesis.Notifications;

/// <summary>
/// What <see cref="NotificationHub"/> does on each hook. Implemented by the service that maps the hub (blocks-logic);
/// a service that only sends needs no implementation.
/// </summary>
public interface INotificationHubHandler
{
    /// <summary>A client connected: store the connection and add it to its groups (<see cref="NotificationGroups"/>).</summary>
    Task OnConnectedAsync(HubCallerContext context, IGroupManager groups);

    /// <summary>A client disconnected (<paramref name="exception"/> is null on a clean close).</summary>
    Task OnDisconnectedAsync(HubCallerContext context, Exception? exception);

    /// <summary>Client hub method <c>Subscribe(command)</c>.</summary>
    Task SubscribeAsync(HubCallerContext context, string command);

    /// <summary>Client hub method <c>Unsubscribe(command)</c>.</summary>
    Task UnsubscribeAsync(HubCallerContext context, string command);
}
