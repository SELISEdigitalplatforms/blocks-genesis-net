using Blocks.Genesis;

namespace WorkerOne
{
    internal class W1Consumer : IConsumer<W1Context>
    {
        private readonly ILogger<W1Consumer> _logger;
        private readonly IHttpService _httpService;

        public W1Consumer(ILogger<W1Consumer> logger, IHttpService httpService)
        {
            _logger = logger;
            _httpService = httpService;
        }
        public async Task Consume(W1Context context)
        {
            _logger.LogInformation("Message recieved from W1");

            var sc = BlocksContext.GetContext();
            Console.WriteLine(sc.IsAuthenticated);
            // Make HTTP call to S2
            //var response = await _httpService.Get<object>("http://localhost:51846/api/s2/process",
            //    new Dictionary<string, string> { { BlocksConstants.BlocksKey, "f080a1bea04280a72149fd689d50a48c" } });
            _logger.LogInformation("S1 call to S2");

        }

    }

    public record W1Context
    {
        public string Data { get; set; }
    }
}
