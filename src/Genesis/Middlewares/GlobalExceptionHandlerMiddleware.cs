using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Blocks.Genesis
{
    public class GlobalExceptionHandlerMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<GlobalExceptionHandlerMiddleware> _logger;

        public const string EmptyJsonBodyString = "Empty";
        public const string JsonContentType = "application/json";

        public GlobalExceptionHandlerMiddleware(RequestDelegate next, ILogger<GlobalExceptionHandlerMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task Invoke(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (Exception exception)
            {
                await HandleExceptionAsync(context, exception);
            }
        }

        private async Task HandleExceptionAsync(HttpContext context, Exception exception)
        {
            var requestPayload = EmptyJsonBodyString;

            var logMessage = $"Unhandled exception thrown on request Trace: [{context.TraceIdentifier}] " +
                             $"Method: [{context.Request.Method}] {context.Request.GetDisplayUrl()} : {exception.Message}.\r\n" +
                             $"Payload: {requestPayload}";

            _logger.LogError(exception, "{Message}", logMessage);

            context.Response.ContentType = JsonContentType;
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;

            var errorResponse = new { Message = "An error occurred while processing your request." };

            await JsonSerializer.SerializeAsync(context.Response.Body, errorResponse, new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
