using Microsoft.Extensions.Logging;
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
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;

    private const string ContentType = "application/json";

    public HttpService(IHttpClientFactory httpClientFactory, ILogger<HttpService> logger, ActivitySource activitySource)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _activitySource = activitySource;

        var retryOptions = new RetryStrategyOptions<HttpResponseMessage>
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromSeconds(2),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                .Handle<HttpRequestException>()
                .Handle<TimeoutRejectedException>()
                .HandleResult(response =>
                    response.StatusCode == HttpStatusCode.TooManyRequests ||
                    (int)response.StatusCode >= 500),
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
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(15),
            MinimumThroughput = 8,
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                .Handle<HttpRequestException>()
                .Handle<TimeoutRejectedException>()
                .HandleResult(response =>
                    response.StatusCode == HttpStatusCode.TooManyRequests ||
                    (int)response.StatusCode >= 500),
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

        _pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddTimeout(TimeSpan.FromSeconds(30))
            .AddRetry(retryOptions)
            .AddCircuitBreaker(circuitBreakerOptions)
            .Build();
    }

    public Task<(T, string)> Post<T>(object payload, string url, string contentType = ContentType, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default) where T : class
        => MakeRequest<T>(HttpMethod.Post, url, payload, contentType, headers, cancellationToken);

    public Task<(T, string)> Get<T>(string url, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default) where T : class
        => MakeRequest<T>(HttpMethod.Get, url, null, null, headers, cancellationToken);

    public Task<(T, string)> Put<T>(object payload, string url, string contentType = ContentType, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default) where T : class
        => MakeRequest<T>(HttpMethod.Put, url, payload, contentType, headers, cancellationToken);

    public Task<(T, string)> Delete<T>(string url, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default) where T : class
        => MakeRequest<T>(HttpMethod.Delete, url, null, null, headers, cancellationToken);

    public Task<(T, string)> Patch<T>(object payload, string url, string contentType = ContentType, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default) where T : class
        => MakeRequest<T>(HttpMethod.Patch, url, payload, contentType, headers, cancellationToken);

    public Task<(T, string)> SendRequest<T>(HttpMethod method, string url, object? payload = null, string contentType = ContentType, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default) where T : class
        => MakeRequest<T>(method, url, payload, contentType, headers, cancellationToken);

    public Task<(T, string)> PostFormUrlEncoded<T>(Dictionary<string, string> formData, string url, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default) where T : class
        => MakeRequest<T>(HttpMethod.Post, url, formData, "application/x-www-form-urlencoded", headers, cancellationToken, isFormUrlEncoded: true);

    public Task<(T, string)> SendFormUrlEncoded<T>(HttpMethod method, Dictionary<string, string> formData, string url, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default) where T : class
        => MakeRequest<T>(method, url, formData, "application/x-www-form-urlencoded", headers, cancellationToken, isFormUrlEncoded: true);

    private async Task<(T, string)> MakeRequest<T>(HttpMethod method, string url, object? payload = null,
        string? contentType = ContentType, Dictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default, bool isFormUrlEncoded = false) where T : class
    {
        using var client = _httpClientFactory.CreateClient();
        using var requestActivity = _activitySource.StartActivity("OutgoingHttpRequest", ActivityKind.Client, Activity.Current?.Context ?? default);

        requestActivity?.SetTag("url.full", url);
        requestActivity?.SetTag("server.address", new Uri(url).Host);
        requestActivity?.SetTag("http.request.method", method.Method);
        requestActivity?.SetTag("content.type", contentType ?? string.Empty);

        try
        {
            requestActivity?.Start();

            var response = await _pipeline.ExecuteAsync(async token =>
            {
                using var request = CreateHttpRequest(method, url, payload, contentType, headers, isFormUrlEncoded);
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

    private static HttpRequestMessage CreateHttpRequest(HttpMethod method, string url, object? payload,
        string? contentType, Dictionary<string, string>? headers, bool isFormUrlEncoded = false)
    {
        var request = new HttpRequestMessage(method, url);

        if (payload != null)
        {
            if (isFormUrlEncoded && payload is Dictionary<string, string> formData)
            {
                request.Content = new FormUrlEncodedContent(formData);
            }
            else if (contentType == "application/x-www-form-urlencoded" && payload is Dictionary<string, string> formUrlEncodedData)
            {
                request.Content = new FormUrlEncodedContent(formUrlEncodedData);
            }
            else if (!string.IsNullOrEmpty(contentType))
            {
                request.Content = new StringContent(
                    payload is string payloadString ? payloadString : JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    contentType);
            }
        }

        if (headers != null)
        {
            foreach (var key in headers.Keys)
            {
                request.Headers.TryAddWithoutValidation(key, headers[key]);
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
}
