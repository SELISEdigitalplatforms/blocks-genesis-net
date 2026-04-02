using System.Net.Http.Json;

namespace Blocks.Genesis
{
    public class PlatformSecretProvider : ISecretProvider
    {
        private readonly HttpClient _httpClient;
        private readonly PlatformOptions _options;

        public PlatformSecretProvider(HttpClient httpClient, GenesisSecretOptions options)
        {
            _options = options.Platform
                ?? throw new InvalidOperationException(
                    "Platform options must be configured when using SecretMode.Platform.");

            _httpClient = httpClient;
            _httpClient.BaseAddress = new Uri(_options.BaseUrl);
        }

        public async Task<string?> GetAsync(string key)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "secrets/get");
            request.Headers.Add("clientId", _options.ClientId);
            request.Headers.Add("xBlocksKey", _options.XBlocksKey);
            request.Content = JsonContent.Create(new { key });

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<SecretResponse>();
            return result?.Value;
        }

        private sealed record SecretResponse(string? Value);
    }
}
