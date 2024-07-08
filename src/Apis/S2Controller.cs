using Blocks.Genesis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class S2Controller : ControllerBase
{
    private readonly ILogger<S2Controller> _logger;
    private readonly IMessageClient _messageClient;

    public S2Controller(ILogger<S2Controller> logger, IMessageClient messageClient)
    {
        _logger = logger;
        _messageClient = messageClient;
    }

    [HttpGet("process")]
    [Authorize]
    public async Task<IActionResult> ProcessRequest()
    {
        _logger.LogInformation("Processing request in S2: process");

        await Task.WhenAll(_messageClient.SendToConsumerAsync(new ConsumerMessage<W2Context> { ConsumerName = "demo_queue_1", Payload = new W2Context { Data = "From S2" } }),
        _messageClient.SendToMassConsumerAsync(new ConsumerMessage<W1Context> { ConsumerName = "demo_topic_1", Payload = new W1Context { Data = "From S2" } }));

        return Ok();
    }

    [HttpGet("process_1")]
    public IActionResult Process1Request()
    {
        _logger.LogInformation("Processing request in S2: process_1");
       

        return Ok();
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
