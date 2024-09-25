namespace Blocks.Genesis
{
    public interface IHttpService
    {
        public Task<(T, string)> MakePostRequest<T>(object payload, string url, string contentType = "application/json", Dictionary<string, string> header = null) where T : class;
        public Task<(T, string)> MakeGetRequest<T>(string url, Dictionary<string, string> header = null) where T : class;
    }
}
