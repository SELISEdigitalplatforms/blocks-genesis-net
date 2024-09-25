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
    public async Task<IActionResult> ProcessRequest()
    {
        _logger.LogInformation("Processing request in S2: process");

        var sc = BlocksContext.GetContext();

        return Ok(sc);
    }

    [HttpGet("process_1")]
    public IActionResult Process1Request()
    {
        _logger.LogInformation("Processing request in S2: process_1");

        var sc = BlocksContext.GetContext();

        return Ok(sc);
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
