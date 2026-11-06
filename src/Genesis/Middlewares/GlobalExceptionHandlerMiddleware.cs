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
        var payloadLength = await ReadPayloadLengthAsync(context.Request).ConfigureAwait(false);

        var (statusCode, title, errors) = MapException(exception);
        var requestAborted = context.RequestAborted.IsCancellationRequested;

        // A stream error on a request the client has already abandoned (a reset HTTP/2 stream, a
        // dropped connection) is the client leaving, not a server fault. A BadHttpRequestException
        // (Kestrel's included) is also an IOException but keeps the status MapException gave it.
        // The abort is checked here rather than in MapException because only the context knows it.
        if (requestAborted && exception is IOException and not BadHttpRequestException)
        {
            (statusCode, title, errors) = (StatusCodes.Status499ClientClosedRequest, "Request Cancelled", null);
        }

        var logMessage = $"Unhandled exception Trace:[{context.TraceIdentifier}] Method:[{context.Request.Method}] " +
                         $"{context.Request.GetDisplayUrl()} Status:[{statusCode}] PayloadLength:[{payloadLength}]";

        if (IsClientOutcome(exception, statusCode))
        {
            _logger.LogInformation(exception, "{Message}", logMessage);
        }
        else if (statusCode >= 500)
        {
            _logger.LogError(exception, "{Message}", logMessage);

            // Reported here rather than from an MVC exception filter: this middleware is the
            // innermost handler and never rethrows, so nothing outside it sees the exception, and
            // it already knows which faults are server-side. Anything mapped to a 4xx (including the
            // 499 for an aborted request) is a business or client outcome and is deliberately not reported.
            BlocksRollbar.Report(exception, context, statusCode);
        }
        else
        {
            _logger.LogWarning(exception, "{Message}", logMessage);
        }

        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = statusCode;

        // The status is still set so request metrics and traces show the outcome (499, 4xx or 500),
        // but a client that has gone cannot read a body, and writing one to a dead connection only fails.
        if (requestAborted)
        {
            return;
        }

        context.Response.ContentType = "application/problem+json";

        var problem = new ProblemDetails
        {
            Title = title,
            Status = statusCode,
            Detail = statusCode >= StatusCodes.Status500InternalServerError ? "An unexpected error occurred." : exception.Message,
            Instance = context.TraceIdentifier,
            Type = $"https://httpstatuses.com/{statusCode}"
        };

        if (errors != null)
        {
            problem.Extensions["errors"] = errors;
        }

        if (exception is BlocksForbiddenException forbidden)
        {
            problem.Extensions["code"] = forbidden.Code;
        }

        await context.Response.WriteAsync(JsonSerializer.Serialize(problem), Encoding.UTF8);
    }

    private static (int StatusCode, string Title, IDictionary<string, string[]>? Errors) MapException(Exception exception)
    {
        return exception switch
        {
            BlocksValidationException validation => (StatusCodes.Status400BadRequest, "Validation Failed", validation.Errors),
            BlocksAuthenticationException => (StatusCodes.Status401Unauthorized, "Authentication Failed", null),
            BlocksForbiddenException => (StatusCodes.Status403Forbidden, "Forbidden", null),
            BlocksNotFoundException => (StatusCodes.Status404NotFound, "Resource Not Found", null),
            BlocksRateLimitException => (StatusCodes.Status429TooManyRequests, "Rate Limit Exceeded", null),
            OperationCanceledException => (StatusCodes.Status499ClientClosedRequest, "Request Cancelled", null),
            // Kestrel rejects a truncated, too slow or oversized request with the 4xx it means (400,
            // 408, 413, 431...). Any other status falls through to the default 500 and is reported.
            BadHttpRequestException badRequest when badRequest.StatusCode is >= StatusCodes.Status400BadRequest
                and < StatusCodes.Status500InternalServerError => (badRequest.StatusCode, "Bad Request", null),
            _ => (StatusCodes.Status500InternalServerError, "Internal Server Error", null)
        };
    }

    /// <summary>
    /// A request that was cancelled, abandoned or malformed by the client: logged at Information
    /// and never reported to Rollbar. Only <see cref="OperationCanceledException"/> and stream
    /// errors already mapped to a client status qualify; an <see cref="IOException"/> that is still
    /// a 500 is a server fault.
    /// </summary>
    private static bool IsClientOutcome(Exception exception, int statusCode) =>
        exception is OperationCanceledException ||
        (exception is IOException && statusCode < StatusCodes.Status500InternalServerError);

    /// <summary>
    /// The JSON body length for the log line. Returns 0 when the body is not JSON or not seekable,
    /// and when re-reading it fails with a stream error or cancellation (typically a client that
    /// aborted or under-sent it): the length is only diagnostic and must not turn the handled
    /// exception into a second, unhandled one.
    /// </summary>
    private static async Task<int> ReadPayloadLengthAsync(HttpRequest request)
    {
        if (request.ContentType?.Contains(JsonContentType, StringComparison.OrdinalIgnoreCase) != true ||
            !request.Body.CanSeek)
        {
            return 0;
        }

        try
        {
            request.Body.Position = 0;
            using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
            var requestPayload = await reader.ReadToEndAsync().ConfigureAwait(false);
            request.Body.Position = 0;
            return requestPayload.Length;
        }
        catch (Exception readFailure) when (readFailure is IOException or OperationCanceledException)
        {
            return 0;
        }
    }
}
