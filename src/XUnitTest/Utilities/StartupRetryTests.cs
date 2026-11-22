using Blocks.Genesis;
using Microsoft.Extensions.Logging;
using Moq;

namespace XUnitTest.Utilities;

// The environment-variable test shares a process-wide variable; the class runs its tests one at a time.
public class StartupRetryTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(10);

    private static Mock<ILogger> NewLogger() => new();

    private static void VerifyLogged(Mock<ILogger> logger, LogLevel level, Times times) =>
        logger.Verify(x => x.Log(level, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), times);

    [Fact]
    public async Task ExecuteAsync_ShouldRetryUntilTheAttemptSucceeds_AndLogOneLinePerFailedAttempt()
    {
        var logger = NewLogger();
        var calls = 0;

        await StartupRetry.ExecuteAsync("Thing", _ =>
        {
            if (++calls < 4) throw new InvalidOperationException("not yet");
            return Task.CompletedTask;
        }, logger.Object, _ => true, TimeSpan.FromSeconds(30), Fast, Fast);

        Assert.Equal(4, calls);
        VerifyLogged(logger, LogLevel.Warning, Times.Exactly(3));
        VerifyLogged(logger, LogLevel.Error, Times.Never());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRethrowTheOriginalException_WhenTheWaitIsUsedUp()
    {
        var logger = NewLogger();
        var calls = 0;

        var thrown = await Assert.ThrowsAsync<TimeoutException>(() => StartupRetry.ExecuteAsync("Thing", _ =>
        {
            calls++;
            throw new TimeoutException("down");
        }, logger.Object, _ => true, TimeSpan.FromMilliseconds(300), Fast, Fast));

        Assert.Equal("down", thrown.Message);
        Assert.True(calls > 1);
        VerifyLogged(logger, LogLevel.Error, Times.Once());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotRetry_WhenTheFailureIsNotTransient()
    {
        var calls = 0;

        await Assert.ThrowsAsync<ArgumentException>(() => StartupRetry.ExecuteAsync("Thing", _ =>
        {
            calls++;
            throw new ArgumentException("bad");
        }, NewLogger().Object, ex => ex is not ArgumentException, TimeSpan.FromSeconds(30), Fast, Fast));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldTryOnce_WhenTheWaitIsZero()
    {
        var calls = 0;

        await Assert.ThrowsAsync<TimeoutException>(() => StartupRetry.ExecuteAsync("Thing", _ =>
        {
            calls++;
            throw new TimeoutException();
        }, NewLogger().Object, _ => true, TimeSpan.Zero, Fast, Fast));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotWaitPastTheTotal_WhenTheLastDelayWouldOvershoot()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => StartupRetry.ExecuteAsync("Thing", _ => throw new TimeoutException(),
            NewLogger().Object, _ => true, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"took {watch.Elapsed}");
    }

    [Fact]
    public void ReadTotalWait_ShouldUseTheVariable_AndFallBackToTheDefault()
    {
        var original = Environment.GetEnvironmentVariable(StartupRetry.WaitSecondsVariable);
        try
        {
            Environment.SetEnvironmentVariable(StartupRetry.WaitSecondsVariable, null);
            Assert.Equal(TimeSpan.FromSeconds(120), StartupRetry.ReadTotalWait());

            Environment.SetEnvironmentVariable(StartupRetry.WaitSecondsVariable, "45");
            Assert.Equal(TimeSpan.FromSeconds(45), StartupRetry.ReadTotalWait());

            Environment.SetEnvironmentVariable(StartupRetry.WaitSecondsVariable, "0");
            Assert.Equal(TimeSpan.Zero, StartupRetry.ReadTotalWait());

            Environment.SetEnvironmentVariable(StartupRetry.WaitSecondsVariable, "soon");
            Assert.Equal(TimeSpan.FromSeconds(120), StartupRetry.ReadTotalWait());

            Environment.SetEnvironmentVariable(StartupRetry.WaitSecondsVariable, "-5");
            Assert.Equal(TimeSpan.FromSeconds(120), StartupRetry.ReadTotalWait());
        }
        finally
        {
            Environment.SetEnvironmentVariable(StartupRetry.WaitSecondsVariable, original);
        }
    }

    [Fact]
    public void RabbitMqIsTransient_ShouldRejectBadConfigurationAndAcceptNetworkFailures()
    {
        Assert.False(ConfigureRabbitMq.IsTransient(new UriFormatException()));
        Assert.False(ConfigureRabbitMq.IsTransient(new ArgumentNullException("x")));
        Assert.True(ConfigureRabbitMq.IsTransient(new System.Net.Sockets.SocketException()));
        Assert.True(ConfigureRabbitMq.IsTransient(new AggregateException(new System.Net.Sockets.SocketException())));
        Assert.False(ConfigureRabbitMq.IsTransient(new AggregateException(new UriFormatException())));
    }

    [Fact]
    public void AzureIsTransient_ShouldAcceptNetworkAndServerErrorsOnly()
    {
        Assert.True(ConfigureAzureServiceBus.IsTransient(new HttpRequestException()));
        Assert.True(ConfigureAzureServiceBus.IsTransient(new Azure.RequestFailedException(503, "busy")));
        Assert.True(ConfigureAzureServiceBus.IsTransient(new Azure.RequestFailedException(0, "no connection")));
        Assert.False(ConfigureAzureServiceBus.IsTransient(new Azure.RequestFailedException(401, "denied")));
        Assert.False(ConfigureAzureServiceBus.IsTransient(new InvalidOperationException()));
    }
}
