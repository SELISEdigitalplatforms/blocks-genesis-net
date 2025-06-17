using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Logging;
using System.Text;
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
            string requestPayload = EmptyJsonBodyString;

            // Capture request body (if JSON and not empty)
            if (context.Request.ContentType?.Contains(JsonContentType) == true &&
                context.Request.Body.CanSeek)
            {
                context.Request.Body.Position = 0;
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
                requestPayload = await reader.ReadToEndAsync();
                context.Request.Body.Position = 0;
            }

            // Limit the payload size for logging
            if (!string.IsNullOrEmpty(requestPayload) && requestPayload.Length > 1000)
            {
                requestPayload = requestPayload.Substring(0, 1000) + "... [truncated]";
            }

            var logMessage = $"Unhandled exception thrown on request Trace: [{context.TraceIdentifier}] " +
                             $"Method: [{context.Request.Method}] {context.Request.GetDisplayUrl()} : {exception.Message}.\r\n" +
                             $"Payload: {requestPayload}";

            _logger.LogError(exception, "{Message}", logMessage);

            // Prepare error response
            context.Response.ContentType = JsonContentType;
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;

            var errorResponse = new
            {
                Message = "An error occurred while processing your request.",
                TraceId = context.TraceIdentifier
            };

            var errorJson = JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions { WriteIndented = true });

            if (!context.Response.HasStarted)
            {
                await context.Response.WriteAsync(errorJson, Encoding.UTF8);
            }
        }
    }
}
