using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Blocks.Genesis;

/// <summary>
/// Readiness check for the RabbitMQ connection (I01-WP2). Healthy while the channel that
/// <see cref="IRabbitMqService"/> opened is open. Registered with the "ready" tag only when
/// <see cref="RabbitMqConfiguration"/> is set, so services without RabbitMQ are unaffected.
/// </summary>
public sealed class RabbitMqReadinessCheck : IHealthCheck
{
    private readonly IRabbitMqService _rabbitMq;

    public RabbitMqReadinessCheck(IRabbitMqService rabbitMq)
    {
        _rabbitMq = rabbitMq;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // RabbitMqChannel throws until the channel has been initialized: that is "not ready", not an error.
            return Task.FromResult(_rabbitMq.RabbitMqChannel.IsOpen
                ? HealthCheckResult.Healthy("RabbitMQ channel is open")
                : HealthCheckResult.Unhealthy("RabbitMQ channel is not open"));
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("RabbitMQ channel has not been initialized"));
        }
    }
}
