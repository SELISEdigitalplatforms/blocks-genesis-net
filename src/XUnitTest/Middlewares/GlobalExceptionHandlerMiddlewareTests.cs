using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text;

namespace XUnitTest.Middlewares;

public class GlobalExceptionHandlerMiddlewareTests
{
    [Fact]
    public async Task Invoke_ShouldPassThrough_WhenNoExceptionThrown()
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = new GlobalExceptionHandlerMiddleware(_ => Task.CompletedTask, logger.Object);
        var context = new DefaultHttpContext();

        await middleware.Invoke(context);

        Assert.NotEqual(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
    }

    [Fact]
    public async Task Invoke_ShouldWrite500JsonResponse_WhenExceptionIsThrown()
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = new GlobalExceptionHandlerMiddleware(_ => throw new InvalidOperationException("boom"), logger.Object);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("api.local");
        context.Request.Path = "/v1/orders";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"id\":1}"));
        context.Response.Body = new MemoryStream();

        await middleware.Invoke(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);
        Assert.Contains("An error occurred while processing your request.", body);
        Assert.Contains(context.TraceIdentifier, body);
    }
}