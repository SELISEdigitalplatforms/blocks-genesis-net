using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using System.Net;
using System.Reflection;

namespace XUnitTest.Middlewares;

/// <summary>Coverage for the exception-to-HTTP-status mapping in <c>GlobalExceptionHandlerMiddleware</c>.</summary>
public class GlobalExceptionMapTests
{
    [Theory]
    [InlineData(typeof(BlocksAuthenticationException), StatusCodes.Status401Unauthorized)]
    [InlineData(typeof(BlocksNotFoundException), StatusCodes.Status404NotFound)]
    [InlineData(typeof(BlocksForbiddenException), StatusCodes.Status403Forbidden)]
    [InlineData(typeof(BlocksRateLimitException), StatusCodes.Status429TooManyRequests)]
    [InlineData(typeof(OperationCanceledException), 499)]
    [InlineData(typeof(InvalidOperationException), StatusCodes.Status500InternalServerError)]
    public void MapException_ShouldMapKnownTypesToStatusCodes(Type exceptionType, int expectedStatus)
    {
        var exception = exceptionType == typeof(OperationCanceledException) || exceptionType == typeof(InvalidOperationException)
            ? (Exception)Activator.CreateInstance(exceptionType)!
            : (Exception)Activator.CreateInstance(exceptionType, "boom")!;

        Assert.Equal(expectedStatus, InvokeMap(exception));
    }

    [Fact]
    public void MapException_ShouldMapValidationExceptionTo400_WithErrors()
    {
        var ex = new BlocksValidationException("invalid", new Dictionary<string, string[]> { ["Name"] = ["required"] });
        Assert.Equal(StatusCodes.Status400BadRequest, InvokeMap(ex));
    }

#pragma warning disable CA1707 // same naming as the tests around them
    [Theory]
    [InlineData(StatusCodes.Status400BadRequest, StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status408RequestTimeout, StatusCodes.Status408RequestTimeout)]
    [InlineData(StatusCodes.Status413PayloadTooLarge, StatusCodes.Status413PayloadTooLarge)]
    [InlineData(StatusCodes.Status431RequestHeaderFieldsTooLarge, StatusCodes.Status431RequestHeaderFieldsTooLarge)]
    [InlineData(499, 499)]
    [InlineData(399, StatusCodes.Status500InternalServerError)]
    [InlineData(StatusCodes.Status500InternalServerError, StatusCodes.Status500InternalServerError)]
    [InlineData(StatusCodes.Status503ServiceUnavailable, StatusCodes.Status500InternalServerError)]
    public void MapException_ShouldUseOwnStatus_OnlyForClientBadHttpRequest(int exceptionStatus, int expectedStatus)
    {
        Assert.Equal(expectedStatus, InvokeMap(new BadHttpRequestException("bad request", exceptionStatus)));
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status408RequestTimeout)]
    public void MapException_ShouldMapKestrelBadHttpRequest_LikeItsBaseType(int status)
    {
        Assert.Equal(status, InvokeMap(GlobalExceptionClientAbortTests.CreateKestrelBadHttpRequestException(status)));
    }

    [Fact]
    public void MapException_ShouldKeep500_ForStreamErrors()
    {
        // The aborted-request 499 needs HttpContext and is decided in HandleExceptionAsync, not here.
        Assert.Equal(StatusCodes.Status500InternalServerError, InvokeMap(new IOException("The client reset the request stream.")));
    }

    [Fact]
    public void MapException_ShouldKeep500_ForMongoConnectionErrors()
    {
        // C9 (#205): the 499 for a Mongo connection error on an aborted request is also decided in
        // HandleExceptionAsync; the context-free mapping stays 500.
        var connectionId = new ConnectionId(new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017)));
        var exception = new MongoConnectionException(connectionId, "An exception occurred while receiving a message from the server.", new IOException("Operation canceled."));
        Assert.Equal(StatusCodes.Status500InternalServerError, InvokeMap(exception));
    }
#pragma warning restore CA1707

    private static int InvokeMap(Exception exception)
    {
        var type = typeof(BlocksException).Assembly.GetType("Blocks.Genesis.GlobalExceptionHandlerMiddleware")!;
        var method = type.GetMethod("MapException", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = method.Invoke(null, [exception])!;
        return (int)result.GetType().GetField("Item1")!.GetValue(result)!;
    }
}
