using Blocks.Genesis;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using System.Diagnostics.Metrics;

namespace XUnitTest.Middlewares;

public class RequestMetricsMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ShouldCallNext_AndRecordDuration_WithRouteFromPath_WhenEndpointIsNull()
    {
        var nextInvoked = false;
        var middleware = new RequestMetricsMiddleware(_ =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        });

        var (measurements, listener) = CreateMeasurementCollector();
        using (listener)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/api/items";
            context.Response.StatusCode = StatusCodes.Status200OK;

            await middleware.InvokeAsync(context);
            listener.RecordObservableInstruments();
        }

        Assert.True(nextInvoked);
        Assert.NotEmpty(measurements);
        var tags = measurements[0].tags;
        Assert.Equal("/api/items", tags["route"]);
        Assert.Equal("GET", tags["method"]);
        Assert.Equal(200, tags["status_code"]);
        Assert.True(measurements[0].value >= 0);
    }

    [Fact]
    public async Task InvokeAsync_ShouldUseEndpointDisplayName_WhenEndpointIsSet()
    {
        var middleware = new RequestMetricsMiddleware(_ => Task.CompletedTask);

        var (measurements, listener) = CreateMeasurementCollector();
        using (listener)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = "/irrelevant";
            context.Response.StatusCode = StatusCodes.Status201Created;

            var endpoint = new Endpoint(
                requestDelegate: null,
                metadata: EndpointMetadataCollection.Empty,
                displayName: "POST /v1/orders");
            context.Features.Set<IEndpointFeature>(new StubEndpointFeature(endpoint));

            await middleware.InvokeAsync(context);
        }

        Assert.NotEmpty(measurements);
        Assert.Equal("POST /v1/orders", measurements[0].tags["route"]);
        Assert.Equal("POST", measurements[0].tags["method"]);
        Assert.Equal(201, measurements[0].tags["status_code"]);
    }

    [Fact]
    public async Task InvokeAsync_ShouldEmitEmptyRoute_WhenPathAndEndpointAreUnset()
    {
        var middleware = new RequestMetricsMiddleware(_ => Task.CompletedTask);

        var (measurements, listener) = CreateMeasurementCollector();
        using (listener)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Get;
            // Path is unset (PathString default is null-like empty)
            context.Response.StatusCode = StatusCodes.Status204NoContent;

            await middleware.InvokeAsync(context);
        }

        Assert.NotEmpty(measurements);
        var route = measurements[0].tags["route"]?.ToString();
        Assert.True(string.IsNullOrEmpty(route));
    }

    private static (List<(double value, Dictionary<string, object?> tags)> measurements, MeterListener listener) CreateMeasurementCollector()
    {
        var measurements = new List<(double, Dictionary<string, object?>)>();
        var listener = new MeterListener();

        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "blocks.genesis.http"
                && instrument.Name == "blocks.genesis.http.request.duration")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
        {
            var dict = new Dictionary<string, object?>();
            foreach (var kvp in tags)
            {
                dict[kvp.Key] = kvp.Value;
            }
            measurements.Add((value, dict));
        });

        listener.Start();
        return (measurements, listener);
    }

    private sealed class StubEndpointFeature : IEndpointFeature
    {
        public StubEndpointFeature(Endpoint? endpoint) => Endpoint = endpoint;
        public Endpoint? Endpoint { get; set; }
    }
}
