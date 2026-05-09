namespace Blocks.Genesis;

/// <summary>
/// Configuration options for <see cref="HttpService"/> resilience policies and timeouts.
/// </summary>
public class HttpServiceOptions
{
    /// <summary>
    /// Default timeout (in seconds) for HTTP requests. Default is 30 seconds.
    /// Can be overridden per-request via <see cref="IHttpService"/> method parameters.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum retry attempts for transient failures. Default is 3.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Initial delay (in seconds) between retry attempts. Defaults to 2 seconds with exponential backoff.
    /// </summary>
    public int RetryDelaySeconds { get; set; } = 2;

    /// <summary>
    /// Circuit breaker failure ratio threshold (0.0 - 1.0). Default is 0.5 (50%).
    /// </summary>
    public double CircuitBreakerFailureRatio { get; set; } = 0.5;

    /// <summary>
    /// Circuit breaker sampling duration (in seconds). Default is 30 seconds.
    /// </summary>
    public int CircuitBreakerSamplingDurationSeconds { get; set; } = 30;

    /// <summary>
    /// Circuit breaker break duration (in seconds). Default is 15 seconds.
    /// </summary>
    public int CircuitBreakerBreakDurationSeconds { get; set; } = 15;

    /// <summary>
    /// Minimum throughput required for circuit breaker to evaluate. Default is 8.
    /// </summary>
    public int CircuitBreakerMinimumThroughput { get; set; } = 8;
}
