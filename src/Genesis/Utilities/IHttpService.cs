namespace Blocks.Genesis
{
    public interface IHttpService
    {
        Task<(T, string)> Get<T>(string url, Dictionary<string, string> headers = null, CancellationToken cancellationToken = default) where T : class;
        Task<(T, string)> Post<T>(object payload, string url, string contentType = "application/json", Dictionary<string, string> headers = null, CancellationToken cancellationToken = default) where T : class;
        Task<(T, string)> Put<T>(object payload, string url, string contentType = "application/json", Dictionary<string, string> headers = null, CancellationToken cancellationToken = default) where T : class;
        Task<(T, string)> Delete<T>(string url, Dictionary<string, string> headers = null, CancellationToken cancellationToken = default) where T : class;
        Task<(T, string)> Patch<T>(object payload, string url, string contentType = "application/json", Dictionary<string, string> headers = null, CancellationToken cancellationToken = default) where T : class;
        Task<(T, string)> SendRequest<T>(HttpMethod method, string url, object payload = null, string contentType = "application/json", Dictionary<string, string> headers = null, CancellationToken cancellationToken = default) where T : class;
        Task<(T, string)> PostFormUrlEncoded<T>(Dictionary<string, string> formData, string url, Dictionary<string, string> headers = null, CancellationToken cancellationToken = default) where T : class;
        Task<(T, string)> SendFormUrlEncoded<T>(HttpMethod method, Dictionary<string, string> formData, string url, Dictionary<string, string> headers = null, CancellationToken cancellationToken = default) where T : class;
    }
}
