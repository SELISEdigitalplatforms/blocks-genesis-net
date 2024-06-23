using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class HelloWorldController : ControllerBase
    {
        private readonly ILogger<HelloWorldController> _logger;

        public HelloWorldController(ILogger<HelloWorldController> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Gets the default greeting message.
        /// </summary>
        /// <returns>The greeting message.</returns>
        [HttpGet]
        [Route("Index")]
        [Authorize]
        [ProducesResponseType(typeof(string), 200)]
        public IActionResult Get()
        {
            _logger.LogError("from error");
            _logger.LogCritical("from LogCritical");
            _logger.LogInformation("from LogInformation");
            return Ok("Hello, World!");
        }

        /// <summary>
        /// Gets information about the endpoint.
        /// </summary>
        /// <returns>The information message.</returns>
        [HttpGet]
        [ProducesResponseType(typeof(string), 200)]
        public IActionResult GetInfo()
        {
            return Ok("This is the info endpoint.");
        }
    }
}
