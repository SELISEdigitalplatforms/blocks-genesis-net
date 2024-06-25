using MassTransit;
using Microsoft.AspNetCore.Mvc;

namespace ApiOne
{
    [ApiController]
    [Route("api/[controller]")]
    public class S1Controller : ControllerBase
    {
        private readonly ILogger<S1Controller> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IPublishEndpoint _publishEndpoint;

        public S1Controller(ILogger<S1Controller> logger, IHttpClientFactory httpClientFactory, IPublishEndpoint publishEndpoint)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _publishEndpoint = publishEndpoint;
        }

        [HttpPost("process")]
        public async Task<IActionResult> ProcessRequest([FromBody] string model)
        {
            _logger.LogInformation("Processing request in S1");

            // Simulate some processing
            await Task.Delay(100);

            // Send event to B1
            //await _publishEndpoint.Publish<B1Event>(new { Data = model.Data });
            //_logger.LogInformation("S1 send an event to B1");

            // Make HTTP call to S2
            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync("http://localhost:51846/api/s2/process");
            response.EnsureSuccessStatusCode();
            _logger.LogInformation("S1 call to S2");

            return Ok();
        }
    }

}
