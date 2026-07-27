using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using System.Reflection;

namespace XUnitTest.Auth;

public class ProtectedEndPointResourceFilterTests
{
    private const string ResourceNameItemKey = "ProtectedResourceName";

    [Fact]
    public async Task OnActionExecutionAsync_ShouldStoreResourceName_WhenAttributeIsPresent()
    {
        var context = CreateContext(ControllerDescriptorFor(nameof(SampleEndpoints.Protected)));
        var nextCalled = false;

        await new ProtectedEndPointResourceFilter().OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(CreateExecutedContext(context));
        });

        Assert.True(nextCalled);
        Assert.Equal("svc::sample::read", context.HttpContext.Items[ResourceNameItemKey]);
    }

    [Fact]
    public async Task OnActionExecutionAsync_ShouldSkipStorage_WhenResourceNameIsNull()
    {
        var context = CreateContext(ControllerDescriptorFor(nameof(SampleEndpoints.NullResource)));
        var nextCalled = false;

        await new ProtectedEndPointResourceFilter().OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(CreateExecutedContext(context));
        });

        Assert.True(nextCalled);
        Assert.False(context.HttpContext.Items.ContainsKey(ResourceNameItemKey));
    }

    [Fact]
    public async Task OnActionExecutionAsync_ShouldSkipStorage_WhenAttributeIsMissing()
    {
        var context = CreateContext(ControllerDescriptorFor(nameof(SampleEndpoints.Plain)));
        var nextCalled = false;

        await new ProtectedEndPointResourceFilter().OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(CreateExecutedContext(context));
        });

        Assert.True(nextCalled);
        Assert.False(context.HttpContext.Items.ContainsKey(ResourceNameItemKey));
    }

    [Fact]
    public async Task OnActionExecutionAsync_ShouldSkipStorage_WhenDescriptorIsNotControllerAction()
    {
        var context = CreateContext(new ActionDescriptor());
        var nextCalled = false;

        await new ProtectedEndPointResourceFilter().OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(CreateExecutedContext(context));
        });

        Assert.True(nextCalled);
        Assert.False(context.HttpContext.Items.ContainsKey(ResourceNameItemKey));
    }

    // ---- helpers ----

    private static ActionExecutingContext CreateContext(ActionDescriptor descriptor)
    {
        var actionContext = new Microsoft.AspNetCore.Mvc.ActionContext(new DefaultHttpContext(), new RouteData(), descriptor);
        return new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), new SampleEndpoints());
    }

    private static ActionExecutedContext CreateExecutedContext(ActionExecutingContext context)
    {
        return new ActionExecutedContext(context, [], context.Controller);
    }

    private static ControllerActionDescriptor ControllerDescriptorFor(string methodName)
    {
        return new ControllerActionDescriptor
        {
            ControllerTypeInfo = typeof(SampleEndpoints).GetTypeInfo(),
            MethodInfo = typeof(SampleEndpoints).GetMethod(methodName)!
        };
    }

    private sealed class SampleEndpoints
    {
        [ProtectedEndPoint("svc::sample::read")]
        public void Protected() { }

        [ProtectedEndPoint(null!)]
        public void NullResource() { }

        public void Plain() { }
    }
}
