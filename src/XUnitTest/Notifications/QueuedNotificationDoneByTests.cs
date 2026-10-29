using Blocks.Genesis.Notifications;

namespace XUnitTest.Notifications;

public sealed class QueuedNotificationDoneByTests
{
    private const string Tenant = NotificationMongo.TenantId;

    private static string? DoneBy(NotificationSender sender) => (Sample.Item() with { Sender = sender }).DoneByUserId();

    [Theory]
    [InlineData("")]
    [InlineData(Tenant)]
    public void UserActingInThisTenant_IsDoneBy(string originalTenantId) =>
        Assert.Equal("ali", DoneBy(new NotificationSender("ali", originalTenantId, false)));

    [Fact]
    public void NoUser_IsNobody() => Assert.Null(DoneBy(NotificationSender.None));

    [Fact]
    public void ImpersonatedSession_IsNobody() =>
        Assert.Null(DoneBy(new NotificationSender("ali", NotificationMongo.RootTenantId, true)));

    [Fact]
    public void SenderFromAnotherTenant_IsNobody() =>
        Assert.Null(DoneBy(new NotificationSender("root-admin", NotificationMongo.RootTenantId, false)));
}
