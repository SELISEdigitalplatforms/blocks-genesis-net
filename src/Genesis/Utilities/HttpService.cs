using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Blocks.Genesis;

public class HttpService : IHttpService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpService> _logger;
    private readonly ActivitySource _activitySource;
    private readonly IOptions<HttpServiceOptions> _options;
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;

    private const string ContentType = "application/json";

    private sealed record HttpRequestSpec(
        HttpMethod Method,
        string Url,
        object? Payload,
        string? ContentType,
        Dictionary<string, string>? Headers,
        bool IsFormUrlEncoded,
        int? TimeoutSeconds);

    public HttpService(
        IHttpClientFactory httpClientFactory,
        ILogger<HttpService> logger,
        ActivitySource activitySource,
        IOptions<HttpServiceOptions>? options = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _activitySource = activitySource;
        _options = options ?? Options.Create(new HttpServiceOptions());

        _pipeline = BuildPipeline(TimeSpan.FromSeconds(_options.Value.RequestTimeoutSeconds));
    }

    public Task<(T, string)> Post<T>(object payload, string url, string contentType = ContentType, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default, int? timeoutSeconds = null) where T : class
        => MakeRequest<T>(new HttpRequestSpec(HttpMethod.Post, url, payload, contentType, headers, false, timeoutSeconds), cancellationToken);

    public Task<(T, string)> Get<T>(string url, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default, int? timeoutSeconds = null) where T : class
        => MakeRequest<T>(new HttpRequestSpec(HttpMethod.Get, url, null, null, headers, false, timeoutSeconds), cancellationToken);

    public Task<(T, string)> Put<T>(object payload, string url, string contentType = ContentType, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default, int? timeoutSeconds = null) where T : class
        => MakeRequest<T>(new HttpRequestSpec(HttpMethod.Put, url, payload, contentType, headers, false, timeoutSeconds), cancellationToken);

    public Task<(T, string)> Delete<T>(string url, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default, int? timeoutSeconds = null) where T : class
        => MakeRequest<T>(new HttpRequestSpec(HttpMethod.Delete, url, null, null, headers, false, timeoutSeconds), cancellationToken);

    public Task<(T, string)> Patch<T>(object payload, string url, string contentType = ContentType, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default, int? timeoutSeconds = null) where T : class
        => MakeRequest<T>(new HttpRequestSpec(HttpMethod.Patch, url, payload, contentType, headers, false, timeoutSeconds), cancellationToken);

    public Task<(T, string)> SendRequest<T>(HttpMethod method, string url, object? payload = null, string contentType = ContentType, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default, int? timeoutSeconds = null) where T : class
        => MakeRequest<T>(new HttpRequestSpec(method, url, payload, contentType, headers, false, timeoutSeconds), cancellationToken);

    public Task<(T, string)> PostFormUrlEncoded<T>(Dictionary<string, string> formData, string url, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default, int? timeoutSeconds = null) where T : class
        => MakeRequest<T>(new HttpRequestSpec(HttpMethod.Post, url, formData, "application/x-www-form-urlencoded", headers, true, timeoutSeconds), cancellationToken);

    public Task<(T, string)> SendFormUrlEncoded<T>(HttpMethod method, Dictionary<string, string> formData, string url, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default, int? timeoutSeconds = null) where T : class
        => MakeRequest<T>(new HttpRequestSpec(method, url, formData, "application/x-www-form-urlencoded", headers, true, timeoutSeconds), cancellationToken);

    private async Task<(T, string)> MakeRequest<T>(HttpRequestSpec spec, CancellationToken cancellationToken) where T : class
    {
        using var client = _httpClientFactory.CreateClient();
        using var requestActivity = _activitySource.StartActivity("OutgoingHttpRequest", ActivityKind.Client, Activity.Current?.Context ?? default);

        requestActivity?.SetTag("url.full", spec.Url);
        requestActivity?.SetTag("server.address", new Uri(spec.Url).Host);
        requestActivity?.SetTag("http.request.method", spec.Method.Method);
        requestActivity?.SetTag("content.type", spec.ContentType ?? string.Empty);

        // Log if per-request timeout is being used
        if (spec.TimeoutSeconds.HasValue)
        {
            requestActivity?.SetTag("http.timeout.override_seconds", spec.TimeoutSeconds.Value);
            HttpServiceLog.RequestTimeoutOverride(_logger, spec.TimeoutSeconds.Value);
        }

        try
        {
            requestActivity?.Start();

            // Use per-request timeout if specified, otherwise use the default pipeline
            var response = spec.TimeoutSeconds.HasValue
                ? await ExecuteWithCustomTimeout(spec, cancellationToken)
                : await _pipeline.ExecuteAsync(async token =>
                {
                    using var request = CreateHttpRequest(spec);
                    return await client.SendAsync(request, token).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);

            requestActivity?.SetTag("http.response.status_code", (int)response.StatusCode);
            requestActivity?.SetTag("http.response.size", response.Content.Headers.ContentLength ?? 0);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(responseContent) && typeof(T) == typeof(object))
                {
                    return ((T)new object(), string.Empty);
                }

                try
                {
                    var result = JsonSerializer.Deserialize<T>(responseContent);
                    requestActivity?.SetTag("response.type", typeof(T).Name);

                    HttpServiceLog.ResponseSuccessful(_logger, responseContent.Length);
                    return (result!, string.Empty);
                }
                catch (JsonException ex)
                {
                    HttpServiceLog.ResponseDeserializationFailed(_logger, ex);
                    return (null!, $"Error deserializing response: {ex.Message}");
                }
            }

            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            HttpServiceLog.RequestFailed(_logger, (int)response.StatusCode, errorContent);
            return (null!, errorContent);
        }
        catch (Exception e)
        {
            requestActivity?.SetTag("error.message", e.Message);
            requestActivity?.SetTag("error.type", e.GetType().Name);
            HttpServiceLog.RequestException(_logger, e);
            return (null!, e.Message);
        }
        finally
        {
            requestActivity?.Stop();
        }
    }

    /// <summary>
    /// Executes an HTTP request with a custom timeout by creating a dedicated resilience pipeline.
    /// This allows per-request timeout overrides without affecting the shared pipeline.
    /// </summary>
    private async Task<HttpResponseMessage> ExecuteWithCustomTimeout(HttpRequestSpec spec, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();

        // Create a custom pipeline with the override timeout
        var customPipeline = BuildPipeline(TimeSpan.FromSeconds(spec.TimeoutSeconds!.Value));

        return await customPipeline.ExecuteAsync(async token =>
        {
            using var request = CreateHttpRequest(spec);
            return await client.SendAsync(request, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private ResiliencePipeline<HttpResponseMessage> BuildPipeline(TimeSpan timeout)
    {
        var opts = _options.Value;

        var retryOptions = new RetryStrategyOptions<HttpResponseMessage>
        {
            MaxRetryAttempts = opts.MaxRetryAttempts,
            Delay = TimeSpan.FromSeconds(opts.RetryDelaySeconds),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            ShouldHandle = TransientFailurePredicate(),
            OnRetry = args =>
            {
                HttpServiceLog.HttpRetry(_logger, args.AttemptNumber + 1, args.RetryDelay);
                using var retryActivity = _activitySource.StartActivity("HttpRequestRetry", ActivityKind.Internal, Activity.Current?.Context ?? default);
                retryActivity?.SetTag("retry.count", args.AttemptNumber + 1);
                retryActivity?.SetTag("retry.waitTime", args.RetryDelay.ToString());
                return ValueTask.CompletedTask;
            }
        };

        var circuitBreakerOptions = new CircuitBreakerStrategyOptions<HttpResponseMessage>
        {
            FailureRatio = opts.CircuitBreakerFailureRatio,
            SamplingDuration = TimeSpan.FromSeconds(opts.CircuitBreakerSamplingDurationSeconds),
            BreakDuration = TimeSpan.FromSeconds(opts.CircuitBreakerBreakDurationSeconds),
            MinimumThroughput = opts.CircuitBreakerMinimumThroughput,
            ShouldHandle = TransientFailurePredicate(),
            OnOpened = _ =>
            {
                HttpServiceLog.CircuitOpened(_logger);
                return ValueTask.CompletedTask;
            },
            OnClosed = _ =>
            {
                HttpServiceLog.CircuitClosed(_logger);
                return ValueTask.CompletedTask;
            }
        };

        return new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddTimeout(timeout)
            .AddRetry(retryOptions)
            .AddCircuitBreaker(circuitBreakerOptions)
            .Build();
    }

    private static PredicateBuilder<HttpResponseMessage> TransientFailurePredicate() =>
        new PredicateBuilder<HttpResponseMessage>()
            .Handle<HttpRequestException>()
            .Handle<TimeoutRejectedException>()
            .HandleResult(response =>
                response.StatusCode == HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= 500);

    private static HttpRequestMessage CreateHttpRequest(HttpRequestSpec spec)
    {
        var request = new HttpRequestMessage(spec.Method, spec.Url);

        if (spec.Payload != null)
        {
            if (spec.IsFormUrlEncoded && spec.Payload is Dictionary<string, string> formData)
            {
                request.Content = new FormUrlEncodedContent(formData);
            }
            else if (spec.ContentType == "application/x-www-form-urlencoded" && spec.Payload is Dictionary<string, string> formUrlEncodedData)
            {
                request.Content = new FormUrlEncodedContent(formUrlEncodedData);
            }
            else if (!string.IsNullOrEmpty(spec.ContentType))
            {
                request.Content = new StringContent(
                    spec.Payload is string payloadString ? payloadString : JsonSerializer.Serialize(spec.Payload),
                    Encoding.UTF8,
                    spec.ContentType);
            }
        }

        if (spec.Headers != null)
        {
            foreach (var key in spec.Headers.Keys)
            {
                request.Headers.TryAddWithoutValidation(key, spec.Headers[key]);
            }
        }

        return request;
    }
}

internal static partial class HttpServiceLog
{
    [LoggerMessage(EventId = 5001, Level = LogLevel.Warning, Message = "HTTP retry #{RetryAttempt} after {Delay} due to transient failure.")]
    public static partial void HttpRetry(ILogger logger, int retryAttempt, TimeSpan delay);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Warning, Message = "HTTP circuit breaker opened.")]
    public static partial void CircuitOpened(ILogger logger);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Information, Message = "HTTP circuit breaker closed.")]
    public static partial void CircuitClosed(ILogger logger);

    [LoggerMessage(EventId = 5004, Level = LogLevel.Debug, Message = "Response successful. Content length: {Length}")]
    public static partial void ResponseSuccessful(ILogger logger, int length);

    [LoggerMessage(EventId = 5005, Level = LogLevel.Error, Message = "Error deserializing response.")]
    public static partial void ResponseDeserializationFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 5006, Level = LogLevel.Error, Message = "HTTP request failed with status code {StatusCode}. Error: {Error}")]
    public static partial void RequestFailed(ILogger logger, int statusCode, string error);

    [LoggerMessage(EventId = 5007, Level = LogLevel.Error, Message = "Exception during HTTP request")]
    public static partial void RequestException(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 5008, Level = LogLevel.Information, Message = "HTTP request using custom timeout override: {TimeoutSeconds} seconds")]
    public static partial void RequestTimeoutOverride(ILogger logger, int timeoutSeconds);
}
