using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text.Json;

namespace XUnitTest.Middlewares;

public class GlobalExceptionHandlerMiddlewareCoverageTests
{
    [Fact]
    public async Task Invoke_ShouldReturn499AndLogInformation_WhenRequestIsCancelled()
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = new GlobalExceptionHandlerMiddleware(_ => throw new OperationCanceledException(), logger.Object);
        var context = CreateContext();

        await middleware.Invoke(context);

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, context.Response.StatusCode);
        VerifyLogged(logger, LogLevel.Information);
    }

    [Fact]
    public async Task Invoke_ShouldReturn404AndLogWarning_WhenResourceIsNotFound()
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = new GlobalExceptionHandlerMiddleware(_ => throw new BlocksNotFoundException("missing"), logger.Object);
        var context = CreateContext();

        await middleware.Invoke(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        VerifyLogged(logger, LogLevel.Warning);
    }

    [Fact]
    public async Task Invoke_ShouldIncludeValidationErrors_InProblemDetails()
    {
        var errors = new Dictionary<string, string[]> { ["name"] = ["required"] };
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = new GlobalExceptionHandlerMiddleware(
            _ => throw new BlocksValidationException("invalid", errors), logger.Object);
        var context = CreateContext();

        await middleware.Invoke(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);

        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.True(document.RootElement.TryGetProperty("errors", out var errorsElement));
        Assert.Equal("required", errorsElement.GetProperty("name")[0].GetString());
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static void VerifyLogged(Mock<ILogger<GlobalExceptionHandlerMiddleware>> logger, LogLevel level)
    {
        logger.Verify(l => l.Log(
            level,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("Unhandled exception")),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }
}
