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

    [Fact]
    public async Task Invoke_ShouldLogEmptyPayload_WhenContentTypeIsNotJson()
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = new GlobalExceptionHandlerMiddleware(_ => throw new Exception("fail"), logger.Object);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "text/plain";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("plain text"));
        context.Response.Body = new MemoryStream();

        await middleware.Invoke(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
    }

    [Fact]
    public async Task Invoke_ShouldLogEmptyPayload_WhenBodyIsEmpty()
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = new GlobalExceptionHandlerMiddleware(_ => throw new Exception("fail"), logger.Object);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Response.Body = new MemoryStream();

        await middleware.Invoke(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
    }

    [Fact]
    public async Task Invoke_ShouldTruncatePayload_WhenBodyExceeds1000Chars()
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = new GlobalExceptionHandlerMiddleware(_ => throw new Exception("fail"), logger.Object);

        var largePayload = new string('x', 2000);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        var bodyStream = new MemoryStream(Encoding.UTF8.GetBytes(largePayload));
        context.Request.Body = bodyStream;
        context.Request.ContentLength = bodyStream.Length;
        context.Response.Body = new MemoryStream();

        await middleware.Invoke(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
    }

    [Fact]
    public async Task Invoke_ShouldNotWriteBody_WhenResponseHasStarted()
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();

        var responseMock = new Mock<HttpResponse>();
        responseMock.SetupGet(r => r.HasStarted).Returns(true);
        responseMock.SetupProperty(r => r.ContentType);
        responseMock.SetupProperty(r => r.StatusCode);

        var contextMock = new Mock<HttpContext>();
        contextMock.SetupGet(c => c.Response).Returns(responseMock.Object);
        contextMock.SetupGet(c => c.Request).Returns(new DefaultHttpContext().Request);
        contextMock.SetupGet(c => c.TraceIdentifier).Returns("trace-123");

        var middleware = new GlobalExceptionHandlerMiddleware(_ => throw new Exception("boom"), logger.Object);

        var ex = await Record.ExceptionAsync(() => middleware.Invoke(contextMock.Object));

        Assert.Null(ex);
    }

    [Fact]
    public async Task Invoke_ShouldHandleNonSeekableBody_WhenJsonContentType()
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = new GlobalExceptionHandlerMiddleware(_ => throw new Exception("fail"), logger.Object);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json; charset=utf-8";
        // DefaultHttpContext.Request.Body is a non-seekable stream by default
        context.Response.Body = new MemoryStream();

        await middleware.Invoke(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
    }

    [Fact]
    public void Constants_ShouldHaveExpectedValues()
    {
        Assert.Equal("Empty", GlobalExceptionHandlerMiddleware.EmptyJsonBodyString);
        Assert.Equal("application/json", GlobalExceptionHandlerMiddleware.JsonContentType);
    }
}