using Microsoft.AspNetCore.Mvc;

namespace ApiPlatformExample.Controllers;

// Demonstrates LMT structured logging routed through Serilog → MongoDBDynamicSink.
//
// Transport used for the sink is determined at first flush using this priority:
//   1. Lmt:ConnectionString  in appsettings.json / appsettings.Development.json
//   2. Environment variable  Lmt__ConnectionString  (or  Lmt:ConnectionString)
//   3. Secret named          LmtMessageConnectionString
//   4. MongoDB fallback      (secret LogConnectionString / TraceConnectionString)
//
// Service Bus connection string example:
//   "Lmt:ConnectionString": "Endpoint=sb://<ns>.servicebus.windows.net/;SharedAccessKeyName=...;SharedAccessKey=..."
//
// RabbitMQ connection string example (scheme must be amqp:// or amqps://):
//   "Lmt:ConnectionString": "amqp://user:password@rabbitmq-host:5672/vhost"
//
// Environment variable equivalent (use double-underscore as section separator):
//   Lmt__ConnectionString=amqp://user:password@rabbitmq-host:5672/vhost

[ApiController]
[Route("api/lmt-demo")]
public class LmtDemoController : ControllerBase
{
    private readonly ILogger<LmtDemoController> _logger;

    public LmtDemoController(ILogger<LmtDemoController> logger)
    {
        _logger = logger;
    }

    // POST api/lmt-demo/log
    // Body: { "message": "hello", "userId": "alice" }
    //
    // Emits a structured log entry. The two named placeholders ({Message} and {UserId})
    // are stored as separate properties in the LMT payload so they can be queried later.
    [HttpPost("log")]
    public IActionResult EmitLog([FromBody] LmtLogRequest request)
    {
        _logger.LogInformation(
            "LMT demo log: {Message} from user {UserId}",
            request.Message,
            request.UserId);

        return Ok(new { queued = true });
    }

    // POST api/lmt-demo/log-warning
    [HttpPost("log-warning")]
    public IActionResult EmitWarning([FromBody] LmtLogRequest request)
    {
        _logger.LogWarning(
            "LMT demo warning: {Message} from user {UserId}",
            request.Message,
            request.UserId);

        return Ok(new { queued = true });
    }

    // POST api/lmt-demo/log-error
    [HttpPost("log-error")]
    public IActionResult EmitError([FromBody] LmtLogRequest request)
    {
        var ex = new InvalidOperationException(request.Message);
        _logger.LogError(ex, "LMT demo error from user {UserId}", request.UserId);

        return Ok(new { queued = true });
    }
}

public record LmtLogRequest(string Message, string UserId = "anonymous");
