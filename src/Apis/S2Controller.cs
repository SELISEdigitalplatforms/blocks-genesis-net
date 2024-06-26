using Blocks.Genesis;
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
    public async Task<IActionResult> ProcessRequest()
    {
        _logger.LogInformation("Processing request in S2");
        await _messageClient.SendToConsumerAsync(new ConsumerMessage<W2Context> { ConsumerName = "demo_queue", Payload = new W2Context { Data = "" } });
        await _messageClient.SendToMassConsumerAsync(new ConsumerMessage<W1Context> { ConsumerName = "demo_topic", Payload = new W1Context { Data = "" } });

        // Simulate some processing
        await Task.Delay(100);

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
