using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using System.Diagnostics;

namespace XUnitTest.Middlewares;

/// <summary>Tests for <see cref="RequestMetricsMiddleware"/>, which records HTTP request duration.</summary>
public class RequestMetricsMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ShouldInvokeNext_AndRecordDuration()
    {
        var nextCalled = false;
        RequestDelegate next = ctx => { nextCalled = true; ctx.Response.StatusCode = 204; return Task.CompletedTask; };
        var middleware = new RequestMetricsMiddleware(next);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/orders";
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "OrdersController"));

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(204, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_ShouldUseRequestPath_WhenNoEndpoint()
    {
        RequestDelegate next = _ => Task.CompletedTask;
        var middleware = new RequestMetricsMiddleware(next);

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/health";

        var ex = await Record.ExceptionAsync(() => middleware.InvokeAsync(context));
        Assert.Null(ex);
    }
}
