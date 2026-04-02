using Blocks.Genesis;
using Microsoft.AspNetCore.Mvc;

namespace ApiPlatformExample.Controllers;

[ApiController]
[Route("api/platform-secrets")]
public class PlatformSecretCheckController : ControllerBase
{
    private readonly ISecretProvider _secretProvider;

    public PlatformSecretCheckController(ISecretProvider secretProvider)
    {
        _secretProvider = secretProvider;
    }

    [HttpGet("check/{key}")]
    public async Task<IActionResult> Check(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return BadRequest(new { message = "Secret key is required." });
        }

        var value = await _secretProvider.GetAsync(key);

        if (string.IsNullOrWhiteSpace(value))
        {
            return NotFound(new { key, found = false });
        }

        return Ok(new { key, found = true });
    }
}
