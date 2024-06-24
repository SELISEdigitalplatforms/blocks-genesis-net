using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class S2Controller : ControllerBase
{
    private readonly ILogger<S2Controller> _logger;

    public S2Controller(ILogger<S2Controller> logger)
    {
        _logger = logger;
    }

    [HttpGet("process")]
    public async Task<IActionResult> ProcessRequest()
    {
        _logger.LogInformation("Processing request in S2");

        // Simulate some processing
        await Task.Delay(100);

        return Ok();
    }
}
