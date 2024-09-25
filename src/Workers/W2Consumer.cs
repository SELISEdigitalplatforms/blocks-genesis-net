using Blocks.Genesis;
using Newtonsoft.Json;

namespace WorkerOne
{
    internal class W2Consumer : IConsumer<W2Context>
    {
        private readonly ILogger<W1Consumer> _logger;

        public W2Consumer(ILogger<W1Consumer> logger)
        {
            _logger = logger;
        }

        public async Task Consume(W2Context context)
        {
            var sc = BlocksContext.GetContext();
            _logger.LogInformation("Message recieved in W2 Worker One: {message} ", JsonConvert.SerializeObject(sc) );
        }
    }

    public record W2Context
    {
        public string Data { get; set; }
    }
}
