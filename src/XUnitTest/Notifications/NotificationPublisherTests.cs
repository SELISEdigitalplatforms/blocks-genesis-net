using Blocks.Genesis;
using Blocks.Genesis.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace XUnitTest.Notifications;

[Collection("BlocksAuthStaticState")]
public sealed class NotificationPublisherTests
{
    private readonly NotificationQueue _queue = new(NullLogger<NotificationQueue>.Instance);

    private NotificationPublisher Publisher() => new(_queue, new ManualTime(), NullLogger<NotificationPublisher>.Instance);

    private static BlocksContext Context(bool authenticated, string userId = "actor", string organizationId = "token-org") =>
        BlocksContext.Create("T1", [], userId, authenticated, "", organizationId, DateTime.MinValue, "", [], "", "", "", "", "T1");

    private QueuedNotification PublishAndRead(BlocksContext? context, NotificationRequest request)
    {
        BlocksContext.SetContext(context);
        try
        {
            Publisher().Publish(request);
        }
        finally
        {
            BlocksContext.SetContext(null);
        }

        Assert.True(_queue.Reader.TryRead(out var item));
        return item;
    }

    [Fact]
    public void UserTokenSend_UsesTheTokenOrganization_NotTheRequestOne()
    {
        var item = PublishAndRead(Context(authenticated: true), new NotificationRequest { Name = "n", OrganizationId = "body-org" });

        Assert.Equal("token-org", item.OrganizationId);
        Assert.Equal("T1", item.TenantId);
    }

    [Fact]
    public void ServiceSend_UsesTheRequestOrganization()
    {
        var item = PublishAndRead(Context(authenticated: false, userId: ""), new NotificationRequest { Name = "n", OrganizationId = "body-org" });

        Assert.Equal("body-org", item.OrganizationId);
    }

    [Fact]
    public void SendToAnotherTenant_UsesTheRequestOrganization()
    {
        var item = PublishAndRead(Context(authenticated: true), new NotificationRequest { Name = "n", TenantId = "T2", OrganizationId = "body-org" });

        Assert.Equal("T2", item.TenantId);
        Assert.Equal("body-org", item.OrganizationId);
    }

    [Fact]
    public void Publish_SetsIdAndCapturesSender()
    {
        var request = new NotificationRequest { Name = "n", NamedUserIds = ["u1", "u1", " "], Payload = new { Order = 42 } };

        var item = PublishAndRead(Context(authenticated: true), request);

        Assert.NotEqual(Guid.Empty, request.Id);
        Assert.Equal(request.Id, item.Id);
        Assert.Equal(["u1"], item.NamedUserIds);
        Assert.Equal("{\"Order\":42}", item.PayloadJson);
        Assert.Equal("actor", item.Sender.UserId);
    }

    [Fact]
    public void BadRequests_AreDropped_NeverThrown()
    {
        var cyclic = new Dictionary<string, object>();
        cyclic["self"] = cyclic;

        BlocksContext.SetContext(null);
        Publisher().Publish(null!);
        Publisher().Publish(new NotificationRequest { Name = "no-tenant" });
        Publisher().Publish(new NotificationRequest { Name = "n", TenantId = "T1", Payload = cyclic });
        Publisher().Publish(new NotificationRequest { Name = "n", TenantId = "T1", Payload = new string('x', NotificationPublisher.MaxPayloadChars + 1) });

        Assert.False(_queue.Reader.TryRead(out _));
    }

    [Fact]
    public void FullBuffer_DropsNewItems_AndCountsThem()
    {
        var publisher = Publisher();
        for (var i = 0; i < NotificationQueue.Capacity + 5; i++)
        {
            publisher.Publish(new NotificationRequest { Name = "n", TenantId = "T1" });
        }

        Assert.Equal(NotificationQueue.Capacity, _queue.Reader.Count);
        Assert.Equal(5, _queue.Dropped);
    }
}
