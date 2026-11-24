using System.Net.Sockets;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Blocks.Genesis;

/// <summary>
/// Readiness check for the RabbitMQ connection (I01-WP2, I01-WP16). Healthy while the channel that
/// <see cref="IRabbitMqService"/> opened is open; Unhealthy when it was opened and is now closed.
/// When the channel has not been opened yet (a service that publishes lazily never opens it at start),
/// the broker itself is probed with a TCP connection to the host and port of the configured URI (2 s timeout):
/// reachable is Healthy, because the channel opens on first use. No AMQP channel is opened by the probe.
/// Registered with the "ready" tag only when <see cref="RabbitMqConfiguration"/> is set, so services without
/// RabbitMQ are unaffected.
/// </summary>
public sealed class RabbitMqReadinessCheck : IHealthCheck
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly IRabbitMqService _rabbitMq;
    private readonly MessageConfiguration _config;

    public RabbitMqReadinessCheck(IRabbitMqService rabbitMq, MessageConfiguration config)
    {
        _rabbitMq = rabbitMq;
        _config = config;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // RabbitMqChannel throws until the channel has been initialized: that is "not opened yet", not an error.
            return _rabbitMq.RabbitMqChannel.IsOpen
                ? HealthCheckResult.Healthy("RabbitMQ channel is open")
                : HealthCheckResult.Unhealthy("RabbitMQ channel is not open");
        }
        catch (InvalidOperationException)
        {
            return await ProbeBrokerAsync(cancellationToken);
        }
    }

    private async Task<HealthCheckResult> ProbeBrokerAsync(CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(_config.Connection, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return HealthCheckResult.Unhealthy("RabbitMQ channel has not been initialized and the connection string has no host");

        var port = uri.IsDefaultPort || uri.Port <= 0 ? (string.Equals(uri.Scheme, "amqps", StringComparison.OrdinalIgnoreCase) ? 5671 : 5672) : uri.Port;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(uri.Host, port, timeout.Token);
            return HealthCheckResult.Healthy("RabbitMQ broker reachable; the channel opens on first use");
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
        {
            return HealthCheckResult.Unhealthy("RabbitMQ channel has not been initialized and the broker is not reachable");
        }
    }
}
