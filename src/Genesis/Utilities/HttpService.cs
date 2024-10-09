using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Polly;
using Polly.Retry;
using System.Diagnostics;
using System.Text;

namespace Blocks.Genesis
{
    internal class HttpService : IHttpService
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
                .HandleResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode) // Retry on any non-success HTTP status code
                .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    (result, timeSpan, retryCount, context) =>
                    {
                        _logger.LogWarning($"Request failed. Waiting {timeSpan} before retry {retryCount}. Status code: {result.Result.StatusCode}");
                    });
        }

        public async Task<(T, string)> MakePostRequest<T>(object payload, string url, string contentType = "application/json", Dictionary<string, string> header = null) where T : class
        {
            var securityContext = BlocksContext.GetContext();
            using (var client = _httpClientFactory.CreateClient())
            using (var activity = _activitySource.StartActivity("OutgoingHttpRequest", ActivityKind.Client))
            {
                activity?.SetCustomProperty("TenantId", securityContext?.TenantId);
                activity?.SetCustomProperty("SecurityContext", JsonConvert.SerializeObject(securityContext));
                // Set custom parameters in the activity
                activity?.AddTag("url.full", url);
                activity?.AddTag("payloadType", payload.GetType().Name);
                activity?.AddTag("server.address", new Uri(url).Host);

                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    activity?.AddTag("http.request.method", request.Method.ToString());
                    // Set the request content and headers
                    request.Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, contentType);

                    if (header != null)
                    {
                        foreach (var key in header.Keys)
                        {
                            request.Headers.Add(key, header[key]);
                        }
                    }

                    request.Headers.Add("traceparent", activity.Id);

                    try
                    {
                        // Send the HTTP request with Polly retry policy
                        var response = await _retryPolicy.ExecuteAsync(() => client.SendAsync(request));

                        // Handle success
                        if (response.IsSuccessStatusCode)
                        {
                            var result = JsonConvert.DeserializeObject<T>(await response.Content.ReadAsStringAsync());
                            _logger.LogInformation("Result:  {result}", JsonConvert.SerializeObject(result));
                            return (result, string.Empty);
                        }
                        else
                        {
                            // Log and return failure
                            _logger.LogError("Error: {response}", JsonConvert.SerializeObject(response));
                            return (null, "Operation failed");
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.LogError("Error: {error}", JsonConvert.SerializeObject(e));
                        return (null, e.Message);
                    }
                    finally
                    {
                        activity?.Stop(); // Stop the activity
                    }
                }
            }
        }

        public async Task<(T, string)> MakeGetRequest<T>(string url, Dictionary<string, string> header = null) where T : class
        {
            var securityContext = BlocksContext.GetContext();
            using (var client = _httpClientFactory.CreateClient())
            using (var activity = _activitySource.StartActivity("OutgoingHttpRequest", ActivityKind.Client))
            {
                activity?.SetCustomProperty("TenantId", securityContext?.TenantId);
                activity?.SetCustomProperty("SecurityContext", JsonConvert.SerializeObject(securityContext));
                // Set custom parameters in the activity
                activity?.AddTag("url.full", url);
                activity?.AddTag("server.address", new Uri(url).Host);

                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    activity?.AddTag("http.request.method", request.Method.ToString());

                    if (header != null)
                    {
                        foreach (var key in header.Keys)
                        {
                            request.Headers.Add(key, header[key]);
                        }
                    }
                    request.Headers.Add("traceparent", activity.Id);


                    try
                    {
                        // Send the HTTP GET request with Polly retry policy
                        var response = await _retryPolicy.ExecuteAsync(() => client.SendAsync(request));

                        if (response.IsSuccessStatusCode)
                        {
                            var result = JsonConvert.DeserializeObject<T>(await response.Content.ReadAsStringAsync());
                            _logger.LogInformation("Result:  {result}", JsonConvert.SerializeObject(result));
                            return (result, string.Empty);
                        }
                        else
                        {
                            _logger.LogError("Error: {response}", JsonConvert.SerializeObject(response));
                            return (null, "Operation failed");
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.LogError("Error: {error}", JsonConvert.SerializeObject(e));
                        return (null, e.Message);
                    }
                    finally
                    {
                        activity.Stop(); // Stop the activity
                    }
                }
            }
        }
    }
}
