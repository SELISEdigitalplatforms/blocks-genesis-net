using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace Blocks.Genesis;

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
        var payloadLength = 0;
        if (context.Request.ContentType?.Contains(JsonContentType, StringComparison.OrdinalIgnoreCase) == true &&
            context.Request.Body.CanSeek)
        {
            context.Request.Body.Position = 0;
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
            var requestPayload = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;
            payloadLength = requestPayload.Length;
        }

        var (statusCode, title, errors) = MapException(exception);
        var logMessage = $"Unhandled exception Trace:[{context.TraceIdentifier}] Method:[{context.Request.Method}] " +
                         $"{context.Request.GetDisplayUrl()} Status:[{statusCode}] PayloadLength:[{payloadLength}]";

        if (exception is OperationCanceledException)
        {
            _logger.LogInformation(exception, "{Message}", logMessage);
        }
        else if (statusCode >= 500)
        {
            _logger.LogError(exception, "{Message}", logMessage);
        }
        else
        {
            _logger.LogWarning(exception, "{Message}", logMessage);
        }

        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.ContentType = "application/problem+json";
        context.Response.StatusCode = statusCode;

        var problem = new ProblemDetails
        {
            Title = title,
            Status = statusCode,
            Detail = exception.Message,
            Instance = context.TraceIdentifier,
            Type = $"https://httpstatuses.com/{statusCode}"
        };

        if (errors != null)
        {
            problem.Extensions["errors"] = errors;
        }

        await context.Response.WriteAsync(JsonSerializer.Serialize(problem), Encoding.UTF8);
    }

    private static (int StatusCode, string Title, IDictionary<string, string[]>? Errors) MapException(Exception exception)
    {
        return exception switch
        {
            BlocksValidationException validation => (StatusCodes.Status400BadRequest, "Validation Failed", validation.Errors),
            BlocksAuthenticationException => (StatusCodes.Status401Unauthorized, "Authentication Failed", null),
            BlocksNotFoundException => (StatusCodes.Status404NotFound, "Resource Not Found", null),
            BlocksRateLimitException => (StatusCodes.Status429TooManyRequests, "Rate Limit Exceeded", null),
            OperationCanceledException => (StatusCodes.Status499ClientClosedRequest, "Request Cancelled", null),
            _ => (StatusCodes.Status500InternalServerError, "Internal Server Error", null)
        };
    }
}
