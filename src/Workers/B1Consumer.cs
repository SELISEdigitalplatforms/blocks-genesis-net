using MassTransit;

namespace WorkerOne
{
    public class B1Consumer : IConsumer<B1Event>
    {
        private readonly ILogger<B1Consumer> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IPublishEndpoint _publishEndpoint;

        public B1Consumer(ILogger<B1Consumer> logger, IHttpClientFactory httpClientFactory, IPublishEndpoint publishEndpoint)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _publishEndpoint = publishEndpoint;
        }

        public async Task Consume(ConsumeContext<B1Event> context)
        {
            _logger.LogInformation("B1 received event from S1");

            // Simulate some processing
            await Task.Delay(100);

            // Make HTTP call to S2
            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync("http://localhost:51846/api/s2/process");
            response.EnsureSuccessStatusCode();
            _logger.LogInformation("B1 call to S2");

            // Send event to B2
            await _publishEndpoint.Publish(new B2Event { Data = context.Message.Data });
            _logger.LogInformation("B1 send an event to B2");
        }
    }

    public class B1Event
    {
        public string Data { get; set; }
    }

    public class B2Event
    {
        public string Data { get; set; }
    }
}
