using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using System.Reflection;

namespace XUnitTest.Middlewares;

/// <summary>Coverage for the exception-to-HTTP-status mapping in <c>GlobalExceptionHandlerMiddleware</c>.</summary>
public class GlobalExceptionMapTests
{
    [Theory]
    [InlineData(typeof(BlocksAuthenticationException), StatusCodes.Status401Unauthorized)]
    [InlineData(typeof(BlocksNotFoundException), StatusCodes.Status404NotFound)]
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

    private static int InvokeMap(Exception exception)
    {
        var type = typeof(BlocksException).Assembly.GetType("Blocks.Genesis.GlobalExceptionHandlerMiddleware")!;
        var method = type.GetMethod("MapException", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = method.Invoke(null, [exception])!;
        return (int)result.GetType().GetField("Item1")!.GetValue(result)!;
    }
}
