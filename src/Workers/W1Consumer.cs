using Blocks.Genesis;

namespace WorkerOne
{
    internal class W1Consumer : IConsumer<W1Context>
    {
        private readonly ILogger<W1Consumer> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        public W1Consumer(ILogger<W1Consumer> logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }
        public async Task Consume(W1Context context)
        {
            _logger.LogInformation("Message recieved from W1");

            // Make HTTP call to S2
            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync("http://localhost:51846/api/s2/process_1");
            response.EnsureSuccessStatusCode();
            _logger.LogInformation("S1 call to S2");

        }

    }

    public record W1Context
    {
        public string Data { get; set; }
    }
}
