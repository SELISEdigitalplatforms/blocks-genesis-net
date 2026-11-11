using Blocks.Genesis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using Moq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;

namespace XUnitTest.Middlewares;

#pragma warning disable CA1707, CA2007, CA1873 // same naming, await style and logger.Verify pattern as the middleware tests next to this file

/// <summary>
/// A <c>MongoConnectionException</c> on a client-aborted request (blocks-genesis-net#205, Rollbar #1592)
/// gets #189's outcome: 499, Information, no Rollbar report, no body. Rollbar is never initialised in the
/// test run and the middleware reports exactly on its Error branch, so "not reported" is asserted as
/// "no Error entry" and "reported" as "one Error entry", as in <see cref="GlobalExceptionClientAbortTests"/>.
/// </summary>
public class GlobalExceptionMongoAbortTests
{
    private const string TraceId = "trace-205";
    private const string GenericServerDetail = "An unexpected error occurred.";
    private const string ReceiveFailure = "An exception occurred while receiving a message from the server.";
    private const string TransportCancelled = "Unable to read data from the transport connection: Operation canceled.";

    private static readonly ConnectionId Connection =
        new(new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017)));

    // H1-H7: every cancellation cause type, directly under the Mongo wrapper or in the #1592 shape.
    [Theory]
    [MemberData(nameof(CancellationCauses))]
    public async Task Invoke_ShouldRecord499WithoutBody_AndNotReport_ForCancelledMongoConnectionOnAbortedRequest(Exception cause)
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = Throwing(new MongoConnectionException(Connection, ReceiveFailure, cause), logger);
        var context = CreateContext(aborted: true);

        var thrown = await Record.ExceptionAsync(() => middleware.Invoke(context));

        Assert.Null(thrown);
        Assert.Equal(StatusCodes.Status499ClientClosedRequest, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
        Assert.Null(context.Response.ContentType);
        VerifyClientOutcomeLogged(logger, StatusCodes.Status499ClientClosedRequest);
        Assert.False(BlocksRollbar.IsEnabled);
    }

    public static TheoryData<Exception> CancellationCauses() => new()
    {
        new IOException(TransportCancelled),                                                       // H1
        new SocketException((int)SocketError.OperationAborted),                                    // H2
        new IOException(TransportCancelled, new SocketException((int)SocketError.OperationAborted)), // H3 (#1592)
        new OperationCanceledException(),                                                          // H4
        new TaskCanceledException(),                                                               // H4
    };

    [Fact]
    public async Task Invoke_ShouldWriteNothingButLog499_WhenResponseHasStarted()
    {
        // H8 / E6
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = Throwing(new MongoConnectionException(Connection, ReceiveFailure, new OperationCanceledException()), logger);
        var context = CreateContext(aborted: true);
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());

        await middleware.Invoke(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Null(context.Response.ContentType);
        Assert.Equal(0, context.Response.Body.Length);
        VerifyClientOutcomeLogged(logger, StatusCodes.Status499ClientClosedRequest);
    }

    [Fact]
    public async Task Invoke_ShouldRecord499_ForMongoConnectionSubtypeOnAbortedRequest()
    {
        // H9: MongoConnectionClosedException only has a (ConnectionId) constructor in driver 3.8.0 and
        // cannot carry a cause, so a test-local subtype stands in for it (spec section 12).
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = Throwing(new ClosedConnectionException(Connection, new IOException(TransportCancelled)), logger);
        var context = CreateContext(aborted: true);

        await middleware.Invoke(context);

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
        Assert.Null(context.Response.ContentType);
        VerifyClientOutcomeLogged(logger, StatusCodes.Status499ClientClosedRequest);
    }

    [Fact]
    public async Task Invoke_ShouldKeepReported500_ForDriverConnectionClosedWithoutCause()
    {
        // The driver's own subtype carries no inner exception, so it has no cancellation cause (C3).
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = Throwing(new MongoConnectionClosedException(Connection), logger);
        var context = CreateContext(aborted: true);

        await middleware.Invoke(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
        VerifyReportedLogged(logger, StatusCodes.Status500InternalServerError);
    }

    [Theory]
    [InlineData(5, StatusCodes.Status499ClientClosedRequest)] // H10
    [InlineData(6, StatusCodes.Status500InternalServerError)] // C5 / E4
    public async Task Invoke_ShouldFindCancellationCause_OnlyWithinFiveInnerLinks(int causeDepth, int expectedStatus)
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = Throwing(new MongoConnectionException(Connection, ReceiveFailure, ChainWithCauseAt(causeDepth)), logger);
        var context = CreateContext(aborted: true);

        await middleware.Invoke(context);

        Assert.Equal(expectedStatus, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
        if (expectedStatus == StatusCodes.Status499ClientClosedRequest)
        {
            VerifyClientOutcomeLogged(logger, expectedStatus);
        }
        else
        {
            VerifyReportedLogged(logger, expectedStatus);
        }
    }

    // C1, C2, C12: on a live request a Mongo connection error is a real outage, whatever its cause.
    [Theory]
    [MemberData(nameof(LiveRequestCauses))]
    public async Task Invoke_ShouldRespondGeneric500AndReport_ForMongoConnectionOnLiveRequest(Exception cause)
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = Throwing(new MongoConnectionException(Connection, ReceiveFailure, cause), logger);
        var context = CreateContext();

        await middleware.Invoke(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        var text = await ReadBodyText(context);
        AssertGeneric500Body(text);
        Assert.DoesNotContain(cause.Message, text, StringComparison.Ordinal);
        VerifyReportedLogged(logger, StatusCodes.Status500InternalServerError);
    }

    public static TheoryData<Exception> LiveRequestCauses() => new()
    {
        new IOException(TransportCancelled),                                                       // C1
        new SocketException((int)SocketError.OperationAborted),                                    // C1
        new IOException(TransportCancelled, new SocketException((int)SocketError.OperationAborted)), // C1 (#1592)
        new OperationCanceledException(),                                                          // C2
    };

    // C3, C4: aborted, but nothing in the chain says the connection was cancelled.
    [Theory]
    [MemberData(nameof(NonCancellationChains))]
    public async Task Invoke_ShouldRecord500WithoutBodyAndReport_ForMongoConnectionWithoutCancellationCause(Exception? inner)
    {
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var exception = inner == null
            ? new MongoConnectionException(Connection, ReceiveFailure)
            : new MongoConnectionException(Connection, ReceiveFailure, inner);
        var middleware = Throwing(exception, logger);
        var context = CreateContext(aborted: true);

        await middleware.Invoke(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
        Assert.Null(context.Response.ContentType);
        VerifyReportedLogged(logger, StatusCodes.Status500InternalServerError);
    }

    public static TheoryData<Exception?> NonCancellationChains() => new()
    {
        null,                                                                          // C3
        new TimeoutException("Timed out after 30000ms."),                              // C4 / E3
        new InvalidOperationException("broken"),                                       // C4
        new AggregateException(new InvalidOperationException("first"), new IOException(TransportCancelled)), // A3: only InnerException (the first) is followed
    };

    [Fact]
    public async Task Invoke_ShouldStopOnSelfReferencingChain_AndReport500()
    {
        // C6: a cycle without a cause. System.Exception has no public setter for InnerException, so the
        // test sets its private field on a test-owned exception (test-only BCL reflection, spec section 12).
        var looping = new InvalidOperationException("looping");
        typeof(Exception).GetField("_innerException", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(looping, looping);
        Assert.Same(looping, looping.InnerException);

        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var middleware = Throwing(new MongoConnectionException(Connection, ReceiveFailure, looping), logger);
        var context = CreateContext(aborted: true);

        // Task.Run, so a walk that never ended would fail the timeout instead of blocking the test thread.
        var thrown = await Record.ExceptionAsync(() => Task.Run(() => middleware.Invoke(context)).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Null(thrown);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
        VerifyReportedLogged(logger, StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public async Task Invoke_ShouldKeepReported500_ForMongoCommandExceptionOnAbortedRequest()
    {
        // C8: other Mongo families are not connection errors, even when they wrap a cancellation cause.
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var command = new MongoCommandException(Connection, "command failed", new BsonDocument("find", "Projects"), new BsonDocument("ok", 0), new IOException(TransportCancelled));
        var middleware = Throwing(command, logger);
        var context = CreateContext(aborted: true);

        await middleware.Invoke(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
        VerifyReportedLogged(logger, StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public async Task Invoke_ShouldClassifyByTypeAndAbortOnly_NotByMessage()
    {
        // C11: an unrelated message still gives 499 on an aborted request ...
        var abortedLogger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var aborted = CreateContext(aborted: true);
        await Throwing(new MongoConnectionException(Connection, "x", new IOException("Disk full")), abortedLogger).Invoke(aborted);

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, aborted.Response.StatusCode);
        VerifyClientOutcomeLogged(abortedLogger, StatusCodes.Status499ClientClosedRequest);

        // ... and the #1592 "Operation canceled" text still gives a reported 500 on a live one.
        var liveLogger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        var live = CreateContext();
        await Throwing(new MongoConnectionException(Connection, ReceiveFailure, new IOException("Operation canceled")), liveLogger).Invoke(live);

        Assert.Equal(StatusCodes.Status500InternalServerError, live.Response.StatusCode);
        VerifyReportedLogged(liveLogger, StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public async Task Pipeline_ShouldRecord499AndNotReport_WhenClientAbortsDuringMongoCall()
    {
        // H11 / section 10 step 1: a real request through TestServer, cancelled by the client while the
        // endpoint is in flight; the endpoint then throws the #1592 shape, as the driver does when the
        // repository call carried no cancellation token.
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        await using var host = await PipelineHost.StartAsync(logger);
        using var client = host.CreateClient();
        using var cancel = new CancellationTokenSource();

        var request = client.GetAsync("/mongo-abort", cancel.Token);
        await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(10)));
        var status = await host.CapturedStatus.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(host.PipelineFault);

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, status);
        VerifyClientOutcomeLogged(logger, StatusCodes.Status499ClientClosedRequest);
    }

    [Fact]
    public async Task Pipeline_ShouldRespondGeneric500AndReport_ForMongoConnectionOnLiveRequest()
    {
        // Section 10 step 2 (C1, C12).
        var logger = new Mock<ILogger<GlobalExceptionHandlerMiddleware>>();
        await using var host = await PipelineHost.StartAsync(logger);
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/mongo-live");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        AssertGeneric500Body(text);
        Assert.DoesNotContain("localhost", text, StringComparison.Ordinal);
        Assert.Equal(StatusCodes.Status500InternalServerError, await host.CapturedStatus.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        VerifyReportedLogged(logger, StatusCodes.Status500InternalServerError);
    }

    /// <summary>The #1592 exception: the driver's receive failure over a cancelled socket read.</summary>
    private static MongoConnectionException Issue1592Exception() =>
        new(Connection, ReceiveFailure, new IOException(TransportCancelled, new SocketException((int)SocketError.OperationAborted)));

    /// <summary>A chain whose only cancellation cause sits at <paramref name="depth"/> InnerException links below the Mongo wrapper.</summary>
    private static Exception ChainWithCauseAt(int depth)
    {
        Exception chain = new IOException(TransportCancelled);
        for (var link = 1; link < depth; link++)
        {
            chain = new InvalidOperationException($"link {link}", chain);
        }

        return chain;
    }

    private static GlobalExceptionHandlerMiddleware Throwing(Exception exception, Mock<ILogger<GlobalExceptionHandlerMiddleware>> logger) =>
        new(_ => throw exception, logger.Object);

    private static DefaultHttpContext CreateContext(bool aborted = false)
    {
        var context = new DefaultHttpContext { TraceIdentifier = TraceId };
        context.Request.Method = HttpMethods.Get;
        context.Response.Body = new MemoryStream();
        context.RequestAborted = new CancellationToken(aborted);
        return context;
    }

    private static async Task<string> ReadBodyText(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    /// <summary>C12: the generic problem body, with no exception, server or inner-exception text.</summary>
    private static void AssertGeneric500Body(string text)
    {
        using var body = JsonDocument.Parse(text);
        Assert.Equal("Internal Server Error", body.RootElement.GetProperty("title").GetString());
        Assert.Equal(StatusCodes.Status500InternalServerError, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(GenericServerDetail, body.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain(ReceiveFailure, text, StringComparison.Ordinal);
        Assert.DoesNotContain(TransportCancelled, text, StringComparison.Ordinal);
        Assert.DoesNotContain("27017", text, StringComparison.Ordinal);
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

    /// <summary>A <c>MongoConnectionException</c> subtype that carries a cause, standing in for <c>MongoConnectionClosedException</c>.</summary>
    private sealed class ClosedConnectionException : MongoConnectionException
    {
        public ClosedConnectionException(ConnectionId connectionId, Exception innerException)
            : base(connectionId, "The connection was closed while receiving a message.", innerException)
        {
        }
    }

    /// <summary>
    /// TestServer host: a status-capture middleware, then <c>GlobalExceptionHandlerMiddleware</c> with the
    /// mocked logger, then the two endpoints from spec section 10.
    /// </summary>
    private sealed class PipelineHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private PipelineHost(WebApplication app) => _app = app;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<int> CapturedStatus { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Anything that escaped the exception middleware (it must not).</summary>
        public Exception? PipelineFault { get; private set; }

        public static async Task<PipelineHost> StartAsync(Mock<ILogger<GlobalExceptionHandlerMiddleware>> logger)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            var app = builder.Build();
            var host = new PipelineHost(app);

            app.Use(async (context, next) =>
            {
                try
                {
                    await next(context);
                }
                catch (Exception fault)
                {
                    host.PipelineFault = fault;
                    throw;
                }
                finally
                {
                    host.CapturedStatus.TrySetResult(context.Response.StatusCode);
                }
            });
            app.Use(next => new GlobalExceptionHandlerMiddleware(next, logger.Object).Invoke);
            app.Map("/mongo-abort", abort => abort.Run(async context =>
            {
                host.Entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, context.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    // The client left; the driver call below did not observe it.
                }

                throw Issue1592Exception();
            }));
            app.Map("/mongo-live", live => live.Run(_ => throw Issue1592Exception()));

            await app.StartAsync();
            return host;
        }

        public HttpClient CreateClient() => _app.GetTestClient();

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}

#pragma warning restore CA1707, CA2007, CA1873
