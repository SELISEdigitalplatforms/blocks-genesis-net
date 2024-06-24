using MassTransit;

namespace WorkerTwo
{
    public class B2Consumer : IConsumer<B2Event>
    {
        private readonly ILogger<B2Consumer> _logger;

        public B2Consumer(ILogger<B2Consumer> logger)
        {
            _logger = logger;
        }

        public async Task Consume(ConsumeContext<B2Event> context)
        {
            _logger.LogInformation("B2 received event from B1");

            // Simulate some processing
            await Task.Delay(100);
        }
    }

    public class B2Event
    {
        public string Data { get; set; }
    }
}
