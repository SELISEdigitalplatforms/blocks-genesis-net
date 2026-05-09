namespace Blocks.Genesis
{
    public interface IHttpService
    {
        /// <summary>
        /// Sends an HTTP GET request.
        /// </summary>
        /// <param name="url">The request URL</param>
        /// <param name="headers">Optional custom headers</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <param name="timeoutSeconds">Optional per-request timeout override (in seconds). If not specified, uses configured default.</param>
        Task<(T, string)> Get<T>(string url, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default, int? timeoutSeconds = null) where T : class;

        /// <summary>
        /// Sends an HTTP POST request with JSON payload.
        /// </summary>
        /// <param name="payload">The request body</param>
        /// <param name="url">The request URL</param>
        /// <param name="contentType">Content type (default: application/json)</param>
        /// <param name="headers">Optional custom headers</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <param name="timeoutSeconds">Optional per-request timeout override (in seconds). If not specified, uses configured default.</param>
        Task<(T, string)> Post<T>(object payload, string url, string contentType = "application/json", Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default, int? timeoutSeconds = null) where T : class;

        /// <summary>
        /// Sends an HTTP PUT request with JSON payload.
        /// </summary>
        /// <param name="payload">The request body</param>
        /// <param name="url">The request URL</param>
        /// <param name="contentType">Content type (default: application/json)</param>
        /// <param name="headers">Optional custom headers</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <param name="timeoutSeconds">Optional per-request timeout override (in seconds). If not specified, uses configured default.</param>
        Task<(T, string)> Put<T>(object payload, string url, string contentType = "application/json", Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default, int? timeoutSeconds = null) where T : class;

        /// <summary>
        /// Sends an HTTP DELETE request.
        /// </summary>
        /// <param name="url">The request URL</param>
        /// <param name="headers">Optional custom headers</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <param name="timeoutSeconds">Optional per-request timeout override (in seconds). If not specified, uses configured default.</param>
        Task<(T, string)> Delete<T>(string url, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default, int? timeoutSeconds = null) where T : class;

        /// <summary>
        /// Sends an HTTP PATCH request with JSON payload.
        /// </summary>
        /// <param name="payload">The request body</param>
        /// <param name="url">The request URL</param>
        /// <param name="contentType">Content type (default: application/json)</param>
        /// <param name="headers">Optional custom headers</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <param name="timeoutSeconds">Optional per-request timeout override (in seconds). If not specified, uses configured default.</param>
        Task<(T, string)> Patch<T>(object payload, string url, string contentType = "application/json", Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default, int? timeoutSeconds = null) where T : class;

        /// <summary>
        /// Sends an HTTP request with the specified method and payload.
        /// </summary>
        /// <param name="method">The HTTP method</param>
        /// <param name="url">The request URL</param>
        /// <param name="payload">Optional request body</param>
        /// <param name="contentType">Content type (default: application/json)</param>
        /// <param name="headers">Optional custom headers</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <param name="timeoutSeconds">Optional per-request timeout override (in seconds). If not specified, uses configured default.</param>
        Task<(T, string)> SendRequest<T>(HttpMethod method, string url, object? payload = null, string contentType = "application/json", Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default, int? timeoutSeconds = null) where T : class;

        /// <summary>
        /// Sends an HTTP POST request with form URL-encoded data.
        /// </summary>
        /// <param name="formData">The form data dictionary</param>
        /// <param name="url">The request URL</param>
        /// <param name="headers">Optional custom headers</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <param name="timeoutSeconds">Optional per-request timeout override (in seconds). If not specified, uses configured default.</param>
        Task<(T, string)> PostFormUrlEncoded<T>(Dictionary<string, string> formData, string url, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default, int? timeoutSeconds = null) where T : class;

        /// <summary>
        /// Sends an HTTP request with form URL-encoded data using the specified method.
        /// </summary>
        /// <param name="method">The HTTP method</param>
        /// <param name="formData">The form data dictionary</param>
        /// <param name="url">The request URL</param>
        /// <param name="headers">Optional custom headers</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <param name="timeoutSeconds">Optional per-request timeout override (in seconds). If not specified, uses configured default.</param>
        Task<(T, string)> SendFormUrlEncoded<T>(HttpMethod method, Dictionary<string, string> formData, string url, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default, int? timeoutSeconds = null) where T : class;
    }
}
