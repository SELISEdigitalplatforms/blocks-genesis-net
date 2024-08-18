using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Text;

namespace Blocks.Genesis.Middlewares
{
    public class GlobalExceptionHandlerMiddleware
    {
        private readonly RequestDelegate next;
        private readonly string errorVerbosity;
        private readonly ILogger<GlobalExceptionHandlerMiddleware> logger;
        private const string BasicErrorMessage = "An error was encountered";
        private static readonly JsonSerializer jsonSerializer = new JsonSerializer();

        public GlobalExceptionHandlerMiddleware(RequestDelegate next, ILogger<GlobalExceptionHandlerMiddleware> logger, string errorVerbosity)
        {
            this.next = next;
            this.logger = logger;
            this.errorVerbosity = errorVerbosity;
        }

        public async Task Invoke(HttpContext context)
        {
            try
            {
                await next(context);
            }
            catch (Exception exception)
            {
                await HandleExceptionAsync(context, exception);
            }
        }

        private async Task HandleExceptionAsync(HttpContext context, Exception exception)
        {
            var jwtToken = GetJwtToken(context);
            var requestPayload = HttpRequestPayloadExtractor.EmptyJsonBodyString;
            var logMessage = $"Unhandled exception thrown on request Trace: [{context.TraceIdentifier}] Method: [{context.Request.Method}] {context.Request.GetDisplayUrl()} : {exception.Message}.\r\nPayload: {requestPayload}\r\nToken: {jwtToken}";
            logger.LogError(exception, "{Message}", logMessage);
            context.Response.ContentType = HttpRequestPayloadExtractor.JsonContentType;
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;

            using (var writer = new HttpResponseStreamWriter(context.Response.Body, Encoding.UTF8))
            {
                switch (errorVerbosity)
                {
                    case ErrorVerbosities.None:
                        jsonSerializer.Serialize(writer, BasicErrorMessage);
                        break;

                    case ErrorVerbosities.ExceptionMessage:
                        jsonSerializer.Serialize(writer, exception.Message);
                        break;

                    case ErrorVerbosities.StackTrace:
                        jsonSerializer.Serialize(writer, exception);
                        break;
                }

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

public static class HttpRequestPayloadExtractor
{
    public const string EmptyJsonBodyString = "Empty";
    public const string JsonContentType = "application/json";
}

public static class ErrorVerbosities
{
    public const string None = "None";
    public const string StackTrace = "StackTrace";
    public const string ExceptionMessage = "ExceptionMessage";
}

