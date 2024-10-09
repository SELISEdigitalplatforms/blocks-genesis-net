using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Text;

namespace Blocks.Genesis
{
    public class GlobalExceptionHandlerMiddleware
    {
        private readonly RequestDelegate _next;
        private static readonly JsonSerializer jsonSerializer = new JsonSerializer();
        private readonly ILogger<GlobalExceptionHandlerMiddleware> _logger;

        public const string EmptyJsonBodyString = "Empty";
        public const string JsonContentType = "application/json";


        public GlobalExceptionHandlerMiddleware(RequestDelegate next, ILogger<GlobalExceptionHandlerMiddleware> logger, IBlocksSecret blocksSecret)
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
            var jwtToken = GetJwtToken(context);
            var requestPayload = EmptyJsonBodyString;
            var logMessage = $"Unhandled exception thrown on request Trace: [{context.TraceIdentifier}] Method: [{context.Request.Method}] {context.Request.GetDisplayUrl()} : {exception.Message}.\r\nPayload: {requestPayload}\r\nToken: {jwtToken}";
            _logger.LogError(exception, "{Message}", logMessage);
            context.Response.ContentType = JsonContentType;
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;

            using (var writer = new HttpResponseStreamWriter(context.Response.Body, Encoding.UTF8))
            {
                jsonSerializer.Serialize(writer, exception.Message);
                await writer.FlushAsync();
            }
        }

        private static string GetJwtToken(HttpContext context)
        {
            return context.User.HasClaim(claim => claim.Type.Equals("OauthBearerToken")) ?
                context.User.Claims.First(c => c.Type.Equals("OauthBearerToken")).Value : string.Empty;
        }
    }
}


