using Blocks.Genesis;

namespace WorkerTwo
{
    internal class W1Consumer : IConsumer<W1Context>
    {
        private readonly ILogger<W1Consumer> _logger;

        public W1Consumer(ILogger<W1Consumer> logger)
        {
            _logger = logger;
        }
        public async Task Consume(W1Context context)
        {
            _logger.LogInformation("Message recieved W1 Worker 2");
        }

    }

    public record W1Context
    {
        public string Data { get; set; }
    }
}
