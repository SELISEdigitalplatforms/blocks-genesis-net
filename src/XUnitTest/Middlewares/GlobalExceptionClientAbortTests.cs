using Blocks.Genesis;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Moq;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace XUnitTest.Middlewares;

#pragma warning disable CA1707, CA2007, CA1873 // same naming, await style and logger.Verify pattern as the middleware tests next to this file

/// <summary>
/// Client-aborted and malformed requests (blocks-genesis-net#189): they get their client status,
/// are logged at Information and are not reported. Rollbar is never initialised in the test run,
/// and the middleware reports exactly on its Error branch, so "not reported" is asserted as "no
/// Error entry" and "reported" as "one Error entry".
/// </summary>
public class GlobalExceptionClientAbortTests
{
    private const string TraceId = "trace-189";
    private const string GenericServerDetail = "An unexpected error occurred.";

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status408RequestTimeout)]
    [InlineData(StatusCodes.Status413PayloadTooLarge)]
    [InlineData(StatusCodes.Status431RequestHeaderFieldsTooLarge)]
    public async Task Invoke_ShouldRespondWithOwnStatus_AndNotReport_ForClientBadHttpRequest(int status)
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = Throwing(new BadHttpRequestException("Unexpected end of request content.", status), logger);
        var context = CreateContext();

        await middleware.Invoke(context);

        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        using var body = await ReadBody(context);
        Assert.Equal("Bad Request", body.RootElement.GetProperty("title").GetString());
        Assert.Equal(status, body.RootElement.GetProperty("status").GetInt32());
        VerifyClientOutcomeLogged(logger, status);
    }

    [Fact]
    public async Task Invoke_ShouldWriteMessageAndTraceId_ForBadHttpRequest()
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = Throwing(new BadHttpRequestException("Unexpected end of request content.", 400), logger);
        var context = CreateContext();

        await middleware.Invoke(context);

        using var body = await ReadBody(context);
        Assert.Equal("Unexpected end of request content.", body.RootElement.GetProperty("detail").GetString());
        Assert.Equal(TraceId, body.RootElement.GetProperty("instance").GetString());
    }

    [Fact]
    public async Task Invoke_ShouldMapKestrelBadHttpRequestType_LikeItsBaseType()
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var kestrelException = CreateKestrelBadHttpRequestException(StatusCodes.Status400BadRequest);
        var middleware = Throwing(kestrelException, logger);
        var context = CreateContext();

        await middleware.Invoke(context);

        Assert.Equal("Microsoft.AspNetCore.Server.Kestrel.Core.BadHttpRequestException", kestrelException.GetType().FullName);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        using var body = await ReadBody(context);
        Assert.Equal("Bad Request", body.RootElement.GetProperty("title").GetString());
        VerifyClientOutcomeLogged(logger, StatusCodes.Status400BadRequest);
    }

    [Theory]
    [InlineData(StatusCodes.Status500InternalServerError)]
    [InlineData(StatusCodes.Status503ServiceUnavailable)]
    [InlineData(StatusCodes.Status302Found)]
    public async Task Invoke_ShouldKeepReported500_ForBadHttpRequestWithNonClientStatus(int status)
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = Throwing(new BadHttpRequestException("internal-detail", status), logger);
        var context = CreateContext();

        await middleware.Invoke(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        using var body = await ReadBody(context);
        Assert.Equal(GenericServerDetail, body.RootElement.GetProperty("detail").GetString());
        VerifyReportedLogged(logger, StatusCodes.Status500InternalServerError);
    }

    [Theory]
    [MemberData(nameof(AbortedStreamExceptions))]
    public async Task Invoke_ShouldRecord499WithoutBody_AndNotReport_ForStreamErrorOnAbortedRequest(Exception exception)
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = Throwing(exception, logger);
        var context = CreateContext(aborted: true);

        var thrown = await Record.ExceptionAsync(() => middleware.Invoke(context));

        Assert.Null(thrown);
        Assert.Equal(StatusCodes.Status499ClientClosedRequest, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
        Assert.Null(context.Response.ContentType);
        VerifyClientOutcomeLogged(logger, StatusCodes.Status499ClientClosedRequest);
        // C7: Rollbar is disabled for the whole test run, and the outcome above is the same.
        Assert.False(BlocksRollbar.IsEnabled);
    }

    public static TheoryData<Exception> AbortedStreamExceptions() => new()
    {
        new IOException("The client reset the request stream."),
        new IOException("The request stream was aborted."),
        new ConnectionResetException("The client reset the request stream."),
    };

    [Fact]
    public async Task Invoke_ShouldKeepBadHttpRequestStatusWithoutBody_WhenRequestIsAborted()
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = Throwing(new BadHttpRequestException("Unexpected end of request content.", 400), logger);
        var context = CreateContext(aborted: true);

        await middleware.Invoke(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
        VerifyClientOutcomeLogged(logger, StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Invoke_ShouldRespondGeneric500AndReport_ForStreamErrorOnLiveRequest()
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = Throwing(new IOException("Disk full"), logger);
        var context = CreateContext();

        await middleware.Invoke(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        using var body = await ReadBody(context);
        Assert.Equal("Internal Server Error", body.RootElement.GetProperty("title").GetString());
        Assert.Equal(GenericServerDetail, body.RootElement.GetProperty("detail").GetString());
        VerifyReportedLogged(logger, StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public async Task Invoke_ShouldClassifyByOuterType_WhenStreamErrorIsWrapped()
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var wrapped = new InvalidOperationException("outer", new IOException("The client reset the request stream."));
        var middleware = Throwing(wrapped, logger);
        var context = CreateContext(aborted: true);

        await middleware.Invoke(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
        VerifyReportedLogged(logger, StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public async Task Invoke_ShouldRecord500WithoutBodyAndReport_ForServerFaultOnAbortedRequest()
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = Throwing(new InvalidOperationException("boom"), logger);
        var context = CreateContext(aborted: true);

        var thrown = await Record.ExceptionAsync(() => middleware.Invoke(context));

        Assert.Null(thrown);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
        VerifyReportedLogged(logger, StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public async Task Invoke_ShouldRecord499WithoutBody_ForCancellationOnAbortedRequest()
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = Throwing(new OperationCanceledException(), logger);
        var context = CreateContext(aborted: true);

        await middleware.Invoke(context);

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
        VerifyClientOutcomeLogged(logger, StatusCodes.Status499ClientClosedRequest);
    }

    [Theory]
    [MemberData(nameof(ClientOutcomesAfterResponseStarted))]
    public async Task Invoke_ShouldWriteNothingButStillLogClientOutcome_WhenResponseHasStarted(Exception exception, bool aborted, int loggedStatus)
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = Throwing(exception, logger);
        var context = CreateContext(aborted);
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());

        await middleware.Invoke(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Null(context.Response.ContentType);
        Assert.Equal(0, context.Response.Body.Length);
        VerifyClientOutcomeLogged(logger, loggedStatus);
    }

    public static TheoryData<Exception, bool, int> ClientOutcomesAfterResponseStarted() => new()
    {
        { new BadHttpRequestException("Unexpected end of request content.", 400), false, StatusCodes.Status400BadRequest },
        { new IOException("The client reset the request stream."), true, StatusCodes.Status499ClientClosedRequest },
    };

    [Theory]
    [MemberData(nameof(AbortedBodyReadFailures))]
    public async Task Invoke_ShouldLogZeroPayloadLength_WhenJsonBodyReadFails(Exception readFailure)
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = Throwing(new BadHttpRequestException("Unexpected end of request content.", 400), logger);
        var context = CreateContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new FailingReadStream(readFailure);

        var thrown = await Record.ExceptionAsync(() => middleware.Invoke(context));

        Assert.Null(thrown);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        VerifyLogged(logger, LogLevel.Information, "PayloadLength:[0]", Times.Once());
    }

    public static TheoryData<Exception> AbortedBodyReadFailures() => new()
    {
        new BadHttpRequestException("Unexpected end of request content.", 400),
        new IOException("The request stream was aborted."),
        new ConnectionResetException("The client reset the request stream."),
        new OperationCanceledException(),
    };

    [Fact]
    public async Task Invoke_ShouldStillLogPayloadLength_WhenJsonBodyIsReadable()
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = Throwing(new BadHttpRequestException("Unexpected end of request content.", 400), logger);
        var context = CreateContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"id\":1}"));

        await middleware.Invoke(context);

        VerifyLogged(logger, LogLevel.Information, "PayloadLength:[8]", Times.Once());
        Assert.Equal(0, context.Request.Body.Position);
    }

    [Fact]
    public async Task Invoke_ShouldNotSwallowOtherBodyReadFailures()
    {
        // Only client-abort read failures are absorbed; anything else behaves as before the change.
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = Throwing(new BadHttpRequestException("Unexpected end of request content.", 400), logger);
        var context = CreateContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new FailingReadStream(new InvalidOperationException("read-bug"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.Invoke(context));
    }

    private static GlobalExceptionHandlerMiddleware Throwing(Exception exception, Mock<ILogger<GlobalExceptionHandlerMiddleware>> logger) =>
        new(_ => throw exception, logger.Object);

    private static DefaultHttpContext CreateContext(bool aborted = false)
    {
        var context = new DefaultHttpContext { TraceIdentifier = TraceId };
        context.Request.Method = HttpMethods.Post;
        context.Response.Body = new MemoryStream();
        context.RequestAborted = new CancellationToken(aborted);
        return context;
    }

    private static async Task<JsonDocument> ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(context.Response.Body);
    }

    /// <summary>The Kestrel type seen in Rollbar is sealed with internal constructors, and obsolete, so it is built by name.</summary>
    internal static Exception CreateKestrelBadHttpRequestException(int status)
    {
        var kestrelType = Type.GetType(
            "Microsoft.AspNetCore.Server.Kestrel.Core.BadHttpRequestException, Microsoft.AspNetCore.Server.Kestrel.Core",
            throwOnError: true)!;
        var constructor = kestrelType
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(c => c.GetParameters().Length == 3);
        var reasonType = constructor.GetParameters()[2].ParameterType;
        var reason = Enum.Parse(reasonType, "UnexpectedEndOfRequestContent");
        return (Exception)constructor.Invoke(["Unexpected end of request content.", status, reason]);
    }

    /// <summary>One Information entry with the client status, and no Warning or Error (so no Rollbar report).</summary>
    private static void VerifyClientOutcomeLogged(Mock<ILogger<GlobalExceptionHandlerMiddleware>> logger, int status)
    {
        VerifyLogged(logger, LogLevel.Information, $"Status:[{status}]", Times.Once());
        VerifyLogged(logger, LogLevel.Warning, "Unhandled exception", Times.Never());
        VerifyLogged(logger, LogLevel.Error, "Unhandled exception", Times.Never());
    }

    /// <summary>One Error entry with the status, the branch that calls <c>BlocksRollbar.Report</c>, and no Information.</summary>
    private static void VerifyReportedLogged(Mock<ILogger<GlobalExceptionHandlerMiddleware>> logger, int status)
    {
        VerifyLogged(logger, LogLevel.Error, $"Status:[{status}]", Times.Once());
        VerifyLogged(logger, LogLevel.Information, "Unhandled exception", Times.Never());
    }

    private static void VerifyLogged(Mock<ILogger<GlobalExceptionHandlerMiddleware>> logger, LogLevel level, string text, Times times)
    {
        logger.Verify(l => l.Log(
            level,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(text, StringComparison.Ordinal)),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), times);
    }

    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    /// <summary>A seekable request body whose read fails, like a buffered body the client stopped sending.</summary>
    private sealed class FailingReadStream(Exception failure) : MemoryStream([1, 2, 3])
    {
        public override int Read(byte[] buffer, int offset, int count) => throw failure;

        public override int Read(Span<byte> buffer) => throw failure;

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException<int>(failure);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(failure);
    }
}

#pragma warning restore CA1707, CA2007, CA1873
