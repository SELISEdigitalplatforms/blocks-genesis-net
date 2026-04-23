using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text.Json;

namespace XUnitTest.Middlewares;

public class GlobalExceptionHandlerMiddlewareAdditionalTests
{
    [Fact]
    public async Task Invoke_ShouldMapValidationException_To400_AndIncludeErrors()
    {
        var errors = new Dictionary<string, string[]>
        {
            ["name"] = ["required"],
            ["age"] = ["must be > 0"]
        };
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = new GlobalExceptionHandlerMiddleware(
            _ => throw new BlocksValidationException("invalid", errors),
            logger.Object);

        var context = CreateContext();
        await middleware.Invoke(context);

        var body = await ReadResponseAsync(context);
        using var doc = JsonDocument.Parse(body);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        Assert.Equal("Validation Failed", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal(400, doc.RootElement.GetProperty("status").GetInt32());
        Assert.True(doc.RootElement.TryGetProperty("errors", out var errorsJson));
        Assert.True(errorsJson.TryGetProperty("name", out _));
        Assert.True(errorsJson.TryGetProperty("age", out _));
    }

    [Fact]
    public async Task Invoke_ShouldMapAuthenticationException_To401()
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = new GlobalExceptionHandlerMiddleware(
            _ => throw new BlocksAuthenticationException("not authenticated"),
            logger.Object);

        var context = CreateContext();
        await middleware.Invoke(context);

        var body = await ReadResponseAsync(context);
        using var doc = JsonDocument.Parse(body);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal("Authentication Failed", doc.RootElement.GetProperty("title").GetString());
        Assert.False(doc.RootElement.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task Invoke_ShouldMapNotFoundException_To404()
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = new GlobalExceptionHandlerMiddleware(
            _ => throw new BlocksNotFoundException("missing"),
            logger.Object);

        var context = CreateContext();
        await middleware.Invoke(context);

        var body = await ReadResponseAsync(context);
        using var doc = JsonDocument.Parse(body);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal("Resource Not Found", doc.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Invoke_ShouldMapRateLimitException_To429()
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = new GlobalExceptionHandlerMiddleware(
            _ => throw new BlocksRateLimitException("slow down"),
            logger.Object);

        var context = CreateContext();
        await middleware.Invoke(context);

        var body = await ReadResponseAsync(context);
        using var doc = JsonDocument.Parse(body);

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        Assert.Equal("Rate Limit Exceeded", doc.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Invoke_ShouldMapOperationCanceledException_To499_AndLogAsInformation()
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = new GlobalExceptionHandlerMiddleware(
            _ => throw new OperationCanceledException("client disconnected"),
            logger.Object);

        var context = CreateContext();
        await middleware.Invoke(context);

        var body = await ReadResponseAsync(context);
        using var doc = JsonDocument.Parse(body);

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, context.Response.StatusCode);
        Assert.Equal("Request Cancelled", doc.RootElement.GetProperty("title").GetString());

        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<OperationCanceledException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("api.local");
        context.Request.Path = "/v1/resource";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ReadResponseAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }
}
