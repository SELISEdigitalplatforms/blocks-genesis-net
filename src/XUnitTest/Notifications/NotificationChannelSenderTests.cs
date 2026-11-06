using Blocks.Genesis;
using Blocks.Genesis.Entities.Logic;
using Blocks.Genesis.Notifications;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Moq;

namespace XUnitTest.Notifications;

public sealed class NotificationChannelSenderTests
{
    private const string Tenant = NotificationMongo.TenantId;
    private const string Org = NotificationMongo.Org;

    private static NotificationDelivery Delivery(NotificationReceivers receivers) =>
        new(Sample.Item(), Sample.Config(), receivers);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Inbox_ProjectRowsInTenantDb_ConsoleRowsInRootDbWithProjectTenant()
    {
        await using var mongo = new NotificationMongo();
        var sender = new NotificationInboxSender(mongo.DbContextProvider.Object, mongo.Secret.Object, mongo.Time);

        await sender.SendAsync(Delivery(new NotificationReceivers(["u1", "u2"], null, ["root-1"])), CancellationToken.None);

        var projectRows = await mongo.TenantDb.GetCollection<OfflineNotification>("OfflineNotifications").Find(_ => true).ToListAsync();
        var consoleRows = await mongo.RootDb.GetCollection<OfflineNotification>("OfflineNotifications").Find(_ => true).ToListAsync();
        Assert.Equal(["u1", "u2"], projectRows.Select(r => r.Payload.UserId).Order());
        Assert.All(projectRows, r => Assert.Null(r.TenantId));
        var consoleRow = Assert.Single(consoleRows);
        Assert.Equal("root-1", consoleRow.Payload.UserId);
        Assert.Equal(Tenant, consoleRow.TenantId);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Inbox_AllUsers_WritesOneRowForTheOrganization()
    {
        await using var mongo = new NotificationMongo();
        var sender = new NotificationInboxSender(mongo.DbContextProvider.Object, mongo.Secret.Object, mongo.Time);

        await sender.SendAsync(Delivery(new NotificationReceivers(["u1", "u2"], Org, [])), CancellationToken.None);

        var row = Assert.Single(await mongo.TenantDb.GetCollection<OfflineNotification>("OfflineNotifications").Find(_ => true).ToListAsync());
        Assert.Null(row.Payload.UserId);
        Assert.Equal(Org, row.OrganizationId);
    }

    private static (NotificationLiveSender Sender, List<(IReadOnlyList<string> Groups, string Method)> Sends) LiveSender(ManualTime time)
    {
        var sends = new List<(IReadOnlyList<string>, string)>();
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Groups(It.IsAny<IReadOnlyList<string>>())).Returns((IReadOnlyList<string> groups) =>
        {
            var proxy = new Mock<IClientProxy>();
            proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                .Callback((string method, object?[] _, CancellationToken _) => sends.Add((groups, method)))
                .Returns(Task.CompletedTask);
            return proxy.Object;
        });
        var hub = new Mock<IHubContext<NotificationHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);
        return (new NotificationLiveSender(hub.Object, time, NullLogger<NotificationLiveSender>.Instance), sends);
    }

    [Fact]
    public async Task Live_SendsToUserGroups_AndOneOrgGroupForAllUsers()
    {
        var (sender, sends) = LiveSender(new ManualTime());

        await sender.SendAsync(Delivery(new NotificationReceivers(["u1"], null, ["root-1"])), CancellationToken.None);
        await sender.SendAsync(Delivery(new NotificationReceivers(["u1", "u2"], Org, [])), CancellationToken.None);

        Assert.Equal([$"tenant:{Tenant}:user:u1", $"tenant:{Tenant}:user:root-1"], sends[0].Groups);
        Assert.Equal([$"tenant:{Tenant}:org:{Org}"], sends[1].Groups);
        Assert.All(sends, s => Assert.Equal("ordersPaid", s.Method));
    }

    [Fact]
    public async Task Live_SkipsItemsOlderThan15Minutes()
    {
        var time = new ManualTime { Now = new DateTimeOffset(Sample.Item().QueuedAt).AddMinutes(16) };
        var (sender, sends) = LiveSender(time);

        await sender.SendAsync(Delivery(new NotificationReceivers(["u1"], null, [])), CancellationToken.None);

        Assert.Empty(sends);
    }

    [Fact]
    public async Task Push_QueuesOneRequestWithEveryReceiverAndTheRootOnes()
    {
        var client = new Mock<IMessageClient>();
        ConsumerMessage<PushRequested>? sent = null;
        client.Setup(c => c.SendToConsumerAsync(It.IsAny<ConsumerMessage<PushRequested>>()))
            .Callback((ConsumerMessage<PushRequested> m) => sent = m)
            .Returns(Task.CompletedTask);
        var delivery = Delivery(new NotificationReceivers(["u1"], Org, ["root-1"]));

        await new NotificationPushSender(client.Object).SendAsync(delivery, CancellationToken.None);

        Assert.Equal("blocks_notification_push_listener", sent!.ConsumerName);
        Assert.Equal(delivery.Item.Id, sent.Payload.Id);
        Assert.Equal(Tenant, sent.Payload.TenantId);
        Assert.Equal("orders-paid", sent.Payload.ConfigName);
        Assert.Equal(["u1", "root-1"], sent.Payload.UserIds);
        Assert.Equal(["root-1"], sent.Payload.RootUserIds);
        Assert.Equal(delivery.Item.PayloadJson, sent.Payload.Payload);
    }

    [Fact]
    public async Task Push_NoUsers_SendsNothing()
    {
        var client = new Mock<IMessageClient>();

        await new NotificationPushSender(client.Object).SendAsync(Delivery(new NotificationReceivers([], Org, [])), CancellationToken.None);

        client.Verify(c => c.SendToConsumerAsync(It.IsAny<ConsumerMessage<PushRequested>>()), Times.Never);
    }
}
