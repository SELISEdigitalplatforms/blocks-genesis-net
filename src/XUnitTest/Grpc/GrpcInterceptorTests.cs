using Blocks.Genesis;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Moq;
using System.Diagnostics;

namespace XUnitTest.Grpc;

public class GrpcInterceptorTests
{
    [Fact]
    public async Task GrpcServerInterceptor_ShouldCallContinuation()
    {
        var interceptor = new GrpcServerInterceptor();
        var request = "hello";
        var metadata = new Metadata
        {
            { "SecurityContext", "{\"TenantId\":\"t1\"}" }
        };
        var context = TestServerCallContext.Create(metadata);

        using var activity = new Activity("grpc-test").Start();

        var response = await interceptor.UnaryServerHandler(
            request,
            context,
            (req, ctx) => Task.FromResult($"echo-{req}"));

        Assert.Equal("echo-hello", response);
    }

    [Fact]
    public async Task GrpcServerInterceptor_ShouldWorkWithoutActivity()
    {
        var interceptor = new GrpcServerInterceptor();
        var context = TestServerCallContext.Create(new Metadata());

        Activity.Current = null;

        var response = await interceptor.UnaryServerHandler(
            "test-input",
            context,
            (req, ctx) => Task.FromResult($"result-{req}"));

        Assert.Equal("result-test-input", response);
    }

    [Fact]
    public void GrpcClientInterceptor_Constructor_ShouldInitialize()
    {
        var activitySource = new ActivitySource("test-grpc");
        var crypto = new Mock<ICryptoService>();
        var tenants = new Mock<ITenants>();

        var interceptor = new GrpcClientInterceptor(activitySource, crypto.Object, tenants.Object);

        Assert.NotNull(interceptor);
    }
}

internal class TestServerCallContext : ServerCallContext
{
    private readonly Metadata _requestHeaders;

    private TestServerCallContext(Metadata requestHeaders)
    {
        _requestHeaders = requestHeaders;
    }

    public static TestServerCallContext Create(Metadata? requestHeaders = null)
    {
        return new TestServerCallContext(requestHeaders ?? new Metadata());
    }

    protected override string MethodCore => "test-method";
    protected override string HostCore => "localhost";
    protected override string PeerCore => "ipv4:127.0.0.1:12345";
    protected override DateTime DeadlineCore => DateTime.UtcNow.AddHours(1);
    protected override Metadata RequestHeadersCore => _requestHeaders;
    protected override CancellationToken CancellationTokenCore => CancellationToken.None;
    protected override Metadata ResponseTrailersCore => new Metadata();
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; }
    protected override AuthContext AuthContextCore => new AuthContext(null, new Dictionary<string, List<AuthProperty>>());

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotImplementedException();
    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
}
