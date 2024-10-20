namespace Blocks.Genesis
{
    public interface IHttpService
    {
        /// <summary>
        /// Makes an HTTP POST request to the specified URL with the provided payload.
        /// </summary>
        /// <typeparam name="T">The type of the expected response.</typeparam>
        /// <param name="payload">The payload object to be serialized and sent with the request.</param>
        /// <param name="url">The URL to which the request will be sent.</param>
        /// <param name="contentType">The content type of the request (default is "application/json").</param>
        /// <param name="header">Optional headers to be added to the request.</param>
        /// <returns>A tuple containing the deserialized response object and an error message, if any.</returns>
        Task<(T, string)> MakePostRequest<T>(object payload, string url, string contentType = "application/json", Dictionary<string, string> header = null) where T : class;

        /// <summary>
        /// Makes an HTTP GET request to the specified URL.
        /// </summary>
        /// <typeparam name="T">The type of the expected response.</typeparam>
        /// <param name="url">The URL to which the request will be sent.</param>
        /// <param name="header">Optional headers to be added to the request.</param>
        /// <returns>A tuple containing the deserialized response object and an error message, if any.</returns>
        Task<(T, string)> MakeGetRequest<T>(string url, Dictionary<string, string> header = null) where T : class;

        /// <summary>
        /// Makes an HTTP PUT request to the specified URL with the provided payload.
        /// </summary>
        /// <typeparam name="T">The type of the expected response.</typeparam>
        /// <param name="payload">The payload object to be serialized and sent with the request.</param>
        /// <param name="url">The URL to which the request will be sent.</param>
        /// <param name="contentType">The content type of the request (default is "application/json").</param>
        /// <param name="header">Optional headers to be added to the request.</param>
        /// <returns>A tuple containing the deserialized response object and an error message, if any.</returns>
        Task<(T, string)> MakePutRequest<T>(object payload, string url, string contentType = "application/json", Dictionary<string, string> header = null) where T : class;

        /// <summary>
        /// Makes an HTTP DELETE request to the specified URL.
        /// </summary>
        /// <typeparam name="T">The type of the expected response.</typeparam>
        /// <param name="url">The URL to which the request will be sent.</param>
        /// <param name="header">Optional headers to be added to the request.</param>
        /// <returns>A tuple containing the deserialized response object and an error message, if any.</returns>
        Task<(T, string)> MakeDeleteRequest<T>(string url, Dictionary<string, string> header = null) where T : class;

        /// <summary>
        /// Makes an HTTP PATCH request to the specified URL with the provided payload.
        /// </summary>
        /// <typeparam name="T">The type of the expected response.</typeparam>
        /// <param name="payload">The payload object to be serialized and sent with the request.</param>
        /// <param name="url">The URL to which the request will be sent.</param>
        /// <param name="contentType">The content type of the request (default is "application/json").</param>
        /// <param name="header">Optional headers to be added to the request.</param>
        /// <returns>A tuple containing the deserialized response object and an error message, if any.</returns>
        Task<(T, string)> MakePatchRequest<T>(object payload, string url, string contentType = "application/json", Dictionary<string, string> header = null) where T : class;
    }
}
