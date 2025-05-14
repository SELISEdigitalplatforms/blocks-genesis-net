using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Web;

namespace Blocks.Genesis
{
    public class HttpService : IHttpService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<HttpService> _logger;
        private readonly ActivitySource _activitySource;
        private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

        public HttpService(IHttpClientFactory httpClientFactory, ILogger<HttpService> logger, ActivitySource activitySource)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _activitySource = activitySource;

            // Define the Polly retry policy (3 retries with exponential backoff)
            _retryPolicy = Policy
                .HandleResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode)
                .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    (result, timeSpan, retryCount, context) =>
                    {
                        _logger.LogWarning($"Request failed. Waiting {timeSpan} before retry {retryCount}. Status code: {result.Result.StatusCode}");

                        using (var retryActivity = _activitySource.StartActivity("HttpRequestRetry", ActivityKind.Internal, Activity.Current?.Context ?? default))
                        {
                            retryActivity?.AddTag("retry.count", retryCount.ToString());
                            retryActivity?.AddTag("retry.waitTime", timeSpan.ToString());
                            retryActivity?.AddTag("retry.statusCode", result.Result.StatusCode.ToString());
                            retryActivity?.AddTag("url.full", context["url"]?.ToString());
                            retryActivity?.Stop();
                        }
                    });
        }

        public async Task<(T, string)> Post<T>(object payload, string url, string contentType = "application/json", Dictionary<string, string> headers = null, CancellationToken cancellationToken = default) where T : class
        {
            return await MakeRequest<T>(HttpMethod.Post, url, payload, contentType, headers, cancellationToken);
        }

        public async Task<(T, string)> Get<T>(string url, Dictionary<string, string> headers = null, CancellationToken cancellationToken = default) where T : class
        {
            return await MakeRequest<T>(HttpMethod.Get, url, null, null, headers, cancellationToken);
        }

        public async Task<(T, string)> Put<T>(object payload, string url, string contentType = "application/json", Dictionary<string, string> headers = null, CancellationToken cancellationToken = default) where T : class
        {
            return await MakeRequest<T>(HttpMethod.Put, url, payload, contentType, headers, cancellationToken);
        }

        public async Task<(T, string)> Delete<T>(string url, Dictionary<string, string> headers = null, CancellationToken cancellationToken = default) where T : class
        {
            return await MakeRequest<T>(HttpMethod.Delete, url, null, null, headers, cancellationToken);
        }

        public async Task<(T, string)> Patch<T>(object payload, string url, string contentType = "application/json", Dictionary<string, string> headers = null, CancellationToken cancellationToken = default) where T : class
        {
            return await MakeRequest<T>(HttpMethod.Patch, url, payload, contentType, headers, cancellationToken);
        }

        /// <summary>
        /// Generic method for making any HTTP request with custom HttpMethod
        /// </summary>
        public async Task<(T, string)> SendRequest<T>(HttpMethod method, string url, object payload = null,
            string contentType = "application/json", Dictionary<string, string> headers = null,
            CancellationToken cancellationToken = default) where T : class
        {
            return await MakeRequest<T>(method, url, payload, contentType, headers, cancellationToken);
        }

        /// <summary>
        /// Makes an HTTP request with form URL encoded data
        /// </summary>
        public async Task<(T, string)> PostFormUrlEncoded<T>(Dictionary<string, string> formData, string url,
            Dictionary<string, string> headers = null, CancellationToken cancellationToken = default) where T : class
        {
            return await MakeRequest<T>(HttpMethod.Post, url, formData, "application/x-www-form-urlencoded",
                headers, cancellationToken, isFormUrlEncoded: true);
        }

        /// <summary>
        /// Makes any HTTP request with form URL encoded data
        /// </summary>
        public async Task<(T, string)> SendFormUrlEncoded<T>(HttpMethod method, Dictionary<string, string> formData,
            string url, Dictionary<string, string> headers = null, CancellationToken cancellationToken = default) where T : class
        {
            return await MakeRequest<T>(method, url, formData, "application/x-www-form-urlencoded",
                headers, cancellationToken, isFormUrlEncoded: true);
        }

        private async Task<(T, string)> MakeRequest<T>(HttpMethod method, string url, object payload = null,
            string contentType = "application/json", Dictionary<string, string> headers = null,
            CancellationToken cancellationToken = default, bool isFormUrlEncoded = false) where T : class
        {
            var securityContext = BlocksContext.GetContext();
            using (var client = _httpClientFactory.CreateClient())
            using (var requestActivity = _activitySource.StartActivity("OutgoingHttpRequest", ActivityKind.Client, Activity.Current?.Context ?? default))
            {
                requestActivity?.SetCustomProperty("TenantId", securityContext?.TenantId);
                requestActivity?.SetCustomProperty("SecurityContext", JsonSerializer.Serialize(securityContext));
                requestActivity?.AddTag("url.full", url);
                requestActivity?.AddTag("server.address", new Uri(url).Host);
                requestActivity?.AddTag("http.request.method", method.Method);
                requestActivity?.AddTag("content.type", contentType);

                try
                {
                    requestActivity?.Start();

                    var response = await _retryPolicy.ExecuteAsync(async context =>
                    {
                        using (var request = CreateHttpRequest(method, url, payload, contentType, headers, isFormUrlEncoded))
                        {
                            return await client.SendAsync(request, cancellationToken);
                        }
                    }, new Context { ["url"] = url });

                    requestActivity?.AddTag("http.response.status_code", response.StatusCode);
                    requestActivity?.AddTag("http.response.size", response.Content.Headers.ContentLength);

                    if (response.IsSuccessStatusCode)
                    {
                        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

                        // Handle empty responses
                        if (string.IsNullOrWhiteSpace(responseContent) && typeof(T) == typeof(object))
                        {
                            return ((T)(object)new object(), string.Empty);
                        }

                        try
                        {
                            var result = JsonSerializer.Deserialize<T>(responseContent);
                            requestActivity?.AddTag("response.type", typeof(T).Name);
                            _logger.LogDebug("Response successful. Content length: {length}", responseContent.Length);
                            return (result, string.Empty);
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogError("Error deserializing response: {error}. Response content: {content}", ex.Message, responseContent);
                            return (null, $"Error deserializing response: {ex.Message}");
                        }
                    }
                    else
                    {
                        var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                        _logger.LogError("HTTP request failed with status code {statusCode}. Error: {error}",
                            response.StatusCode, errorContent);
                        return (null, errorContent);
                    }
                }
                catch (Exception e)
                {
                    requestActivity?.AddTag("error.message", e.Message);
                    requestActivity?.AddTag("error.type", e.GetType().Name);

                    _logger.LogError("Exception during HTTP request: {error}", e);
                    return (null, e.Message);
                }
                finally
                {
                    requestActivity?.Stop();
                }
            }
        }

        private HttpRequestMessage CreateHttpRequest(HttpMethod method, string url, object payload,
            string contentType, Dictionary<string, string> headers, bool isFormUrlEncoded = false)
        {
            var request = new HttpRequestMessage(method, url);

            if (payload != null)
            {
                if (isFormUrlEncoded && payload is Dictionary<string, string> formData)
                {
                    var formContent = new FormUrlEncodedContent(formData);
                    request.Content = formContent;
                }
                else if (contentType == "application/x-www-form-urlencoded" && payload is Dictionary<string, string> formUrlEncodedData)
                {
                    var formContent = new FormUrlEncodedContent(formUrlEncodedData);
                    request.Content = formContent;
                }
                else if (!string.IsNullOrEmpty(contentType))
                {
                    request.Content = new StringContent(
                        payload is string ? payload.ToString() : JsonSerializer.Serialize(payload),
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

        /// <summary>
        /// Helper method to encode a dictionary as x-www-form-urlencoded string
        /// </summary>
        private string EncodeFormData(Dictionary<string, string> formData)
        {
            if (formData == null || formData.Count == 0)
                return string.Empty;

            var stringBuilder = new StringBuilder();
            foreach (var kvp in formData)
            {
                if (stringBuilder.Length > 0)
                    stringBuilder.Append('&');

                stringBuilder.Append(HttpUtility.UrlEncode(kvp.Key));
                stringBuilder.Append('=');
                stringBuilder.Append(HttpUtility.UrlEncode(kvp.Value));
            }

            return stringBuilder.ToString();
        }
    }
}