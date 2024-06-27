using Blocks.Genesis;
using Microsoft.AspNetCore.Mvc;

namespace ApiOne
{
    [ApiController]
    [Route("api/[controller]")]
    public class S1Controller : ControllerBase
    {
        private readonly ILogger<S1Controller> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMessageClient _messageClient;

        public S1Controller(ILogger<S1Controller> logger, IHttpClientFactory httpClientFactory, IMessageClient messageClient)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _messageClient = messageClient;
        }

        [HttpGet("process")]
        public async Task<IActionResult> ProcessRequest()
        {
            _logger.LogInformation("Processing request in S1");

            // Send event to B1
            _messageClient.SendToConsumerAsync(new ConsumerMessage<W2Context> { ConsumerName = "demo_queue", Payload = new W2Context { Data = "From S1" } });
            //await _messageClient.SendToMassConsumerAsync(new ConsumerMessage<W1Context> { ConsumerName = "demo_topic", Payload = new W1Context { Data = "From S1" } });
            _logger.LogInformation("S1 send an event to B1");

            // Make HTTP call to S2
            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync("http://localhost:51846/api/s2/process");
            response.EnsureSuccessStatusCode();
            _logger.LogInformation("S1 call to S2");

            return Ok();
        }
    }

    public record W2Context
    {
        public string Data { get; set; }
    }

    public record W1Context
    {
        public string Data { get; set; }
    }

}
