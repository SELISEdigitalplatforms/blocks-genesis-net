using Blocks.Genesis.Entities.Os;
using Blocks.Genesis.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace XUnitTest.Notifications;

public sealed class NotificationDispatcherTests
{
    private sealed class FakeConfigReader(NotificationConfiguration? configuration) : INotificationConfigReader
    {
        public Task<NotificationConfiguration?> GetAsync(string tenantId, string name, CancellationToken cancellationToken) =>
            Task.FromResult(configuration);
    }

    private sealed class FakeRateLimiter(bool allow) : INotificationRateLimiter
    {
        public int? LastLimit { get; private set; }

        public Task<bool> TryAcquireAsync(string tenantId, string name, int limitPerMinute)
        {
            LastLimit = limitPerMinute;
            return Task.FromResult(allow);
        }
    }

    private sealed class FakeResolver(NotificationReceivers receivers) : INotificationReceiverResolver
    {
        public Task<NotificationReceivers> ResolveAsync(QueuedNotification item, NotificationConfiguration configuration, CancellationToken cancellationToken) =>
            Task.FromResult(receivers);
    }

    private sealed class RecordingChannel(string name, bool fails = false) : INotificationChannelSender
    {
        public List<NotificationDelivery> Sent { get; } = [];

        public string Channel => name;

        public bool IsOn(NotificationChannels channels) => name switch
        {
            "live" => channels.Live,
            "push" => channels.Push,
            _ => channels.Inbox,
        };

        public Task SendAsync(NotificationDelivery delivery, CancellationToken cancellationToken)
        {
            if (fails)
            {
                throw new InvalidOperationException("broker down");
            }

            Sent.Add(delivery);
            return Task.CompletedTask;
        }
    }

    private static readonly NotificationReceivers OneUser = new(["u1"], null, []);

    private static Task DispatchAsync(NotificationConfiguration? configuration, params INotificationChannelSender[] channels) =>
        DispatchAsync(configuration, new FakeRateLimiter(true), channels);

    private static Task DispatchAsync(NotificationConfiguration? configuration, FakeRateLimiter limiter, params INotificationChannelSender[] channels) =>
        new NotificationDispatcher(new FakeConfigReader(configuration), limiter, new FakeResolver(OneUser), channels,
            NullLogger<NotificationDispatcher>.Instance).DispatchAsync(Sample.Item(), CancellationToken.None);

    [Fact]
    public async Task MissingConfiguration_SendsNothing()
    {
        var inbox = new RecordingChannel("inbox");

        await DispatchAsync(null, inbox);

        Assert.Empty(inbox.Sent);
    }

    [Fact]
    public async Task DisabledConfiguration_SendsNothing()
    {
        var inbox = new RecordingChannel("inbox");
        var configuration = Sample.Config();
        configuration.IsDisabled = true;

        await DispatchAsync(configuration, inbox);

        Assert.Empty(inbox.Sent);
    }

    [Fact]
    public async Task DisabledBuiltInJobResult_IsStillSent()
    {
        var inbox = new RecordingChannel("inbox");
        var configuration = Sample.Config();
        configuration.IsBuiltIn = true;
        configuration.Kind = NotificationKind.Job;
        configuration.IsDisabled = true;

        await DispatchAsync(configuration, inbox);

        Assert.Single(inbox.Sent);
    }

    [Fact]
    public async Task OverTheRateLimit_SendsNothing_DefaultLimitIs120()
    {
        var inbox = new RecordingChannel("inbox");
        var limiter = new FakeRateLimiter(false);

        await DispatchAsync(Sample.Config(), limiter, inbox);

        Assert.Empty(inbox.Sent);
        Assert.Equal(120, limiter.LastLimit);
    }

    [Fact]
    public async Task OnlyChannelsThatAreOn_AreUsed()
    {
        var inbox = new RecordingChannel("inbox");
        var live = new RecordingChannel("live");
        var push = new RecordingChannel("push");
        var configuration = Sample.Config();
        configuration.EnablePersistence = false;

        await DispatchAsync(configuration, inbox, live, push);

        Assert.Empty(inbox.Sent);
        Assert.Single(live.Sent);
        Assert.Empty(push.Sent);
    }

    [Fact]
    public async Task FailingChannel_DoesNotStopTheOthers()
    {
        var live = new RecordingChannel("live", fails: true);
        var inbox = new RecordingChannel("inbox");

        await DispatchAsync(Sample.Config(), live, inbox);

        Assert.Single(inbox.Sent);
    }
}
