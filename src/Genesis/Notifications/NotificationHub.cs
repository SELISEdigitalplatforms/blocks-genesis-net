using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Blocks.Genesis.Notifications;

/// <summary>
/// The notification hub. Its full type name is part of the Redis backplane channel names, so senders in other
/// services reach the clients of the service that maps it: never rename or move it, and never subclass it.
/// Every hook is delegated to <see cref="INotificationHubHandler"/>.
/// </summary>
[Authorize]
public sealed class NotificationHub : Hub
{
    private readonly INotificationHubHandler _handler;

    public NotificationHub(INotificationHubHandler handler)
    {
        _handler = handler;
    }

    public override async Task OnConnectedAsync()
    {
        await _handler.OnConnectedAsync(Context, Groups);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await _handler.OnDisconnectedAsync(Context, exception);
        await base.OnDisconnectedAsync(exception);
    }

    public Task Subscribe(string command) => _handler.SubscribeAsync(Context, command);

    public Task Unsubscribe(string command) => _handler.UnsubscribeAsync(Context, command);
}
