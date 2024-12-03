using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

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

                        // Add activity for each retry attempt
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

        public async Task<(T, string)> Post<T>(object payload, string url, string contentType = "application/json", Dictionary<string, string> header = null) where T : class
        {
            return await MakeRequest<T>(HttpMethod.Post, url, payload, contentType, header);
        }

        public async Task<(T, string)> Get<T>(string url, Dictionary<string, string> header = null) where T : class
        {
            return await MakeRequest<T>(HttpMethod.Get, url, null, null, header);
        }

        public async Task<(T, string)> Put<T>(object payload, string url, string contentType = "application/json", Dictionary<string, string> header = null) where T : class
        {
            return await MakeRequest<T>(HttpMethod.Put, url, payload, contentType, header);
        }

        public async Task<(T, string)> Delete<T>(string url, Dictionary<string, string> header = null) where T : class
        {
            return await MakeRequest<T>(HttpMethod.Delete, url, null, null, header);
        }

        public async Task<(T, string)> Patch<T>(object payload, string url, string contentType = "application/json", Dictionary<string, string> header = null) where T : class
        {
            return await MakeRequest<T>(HttpMethod.Patch, url, payload, contentType, header);
        }

        private async Task<(T, string)> MakeRequest<T>(HttpMethod method, string url, object payload = null, string contentType = "application/json", Dictionary<string, string> header = null) where T : class
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

                try
                {
                    requestActivity?.Start();

                    var response = await _retryPolicy.ExecuteAsync(async context =>
                    {
                        using (var request = CreateHttpRequest(method, url, payload, contentType, header))
                        {
                            return await client.SendAsync(request);
                        }
                    }, new Context { ["url"] = url });

                    requestActivity?.AddTag("http.response.status_code", response.StatusCode);
                    requestActivity?.AddTag("http.response.size", response.Content.Headers.ContentLength);

                    if (response.IsSuccessStatusCode)
                    {
                        var result = JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync());
                        requestActivity?.AddTag("response.type", typeof(T).Name);

                        _logger.LogInformation("Result: {result}", JsonSerializer.Serialize(result));
                        return (result, string.Empty);
                    }
                    else
                    {
                        _logger.LogError("Error: {response}", JsonSerializer.Serialize(response));
                        return (null, "Operation failed");
                    }
                }
                catch (Exception e)
                {
                    requestActivity?.AddTag("error.message", e.Message);
                    requestActivity?.AddTag("error.type", e.GetType().Name);

                    _logger.LogError("Error: {error}", e);
                    return (null, e.Message);
                }
                finally
                {
                    requestActivity?.Stop();
                }
            }
        }


        private HttpRequestMessage CreateHttpRequest(HttpMethod method, string url, object payload, string contentType, Dictionary<string, string> header)
        {
            var request = new HttpRequestMessage(method, url);

            if (payload != null)
            {
                request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, contentType);
            }

            if (header != null)
            {
                foreach (var key in header.Keys)
                {
                    request.Headers.Add(key, header[key]);
                }
            }

            return request;
        }
    }
}
