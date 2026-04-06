using Blocks.Genesis;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Moq;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

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

    [Fact]
    public async Task GrpcClientInterceptor_AsyncUnaryCall_ShouldAddHeaders_AndReturnResponse()
    {
        var activitySource = new ActivitySource("test-grpc-client-success");
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "test-grpc-client-success",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        var crypto = new Mock<ICryptoService>();
        var tenants = new Mock<ITenants>();

        tenants.Setup(t => t.GetTenantByID("tenant-1"))
            .Returns(new Blocks.Genesis.Tenant
            {
                ApplicationDomain = "app.example",
                DbConnectionString = "mongodb://localhost",
                JwtTokenParameters = new JwtTokenParameters
                {
                    PrivateCertificatePassword = "p",
                    IssueDate = DateTime.UtcNow
                },
                TenantSalt = "salt-1"
            });

        crypto.Setup(c => c.Hash("tenant-1", "salt-1")).Returns("hash-tenant-1");

        var interceptor = new GrpcClientInterceptor(activitySource, crypto.Object, tenants.Object);
        var method = CreateStringMethod();
        var headers = new Metadata { { "existing", "yes" } };
        var callOptions = new CallOptions(headers).WithDeadline(DateTime.UtcNow.AddMinutes(1)).WithWaitForReady(true);
        var context = new ClientInterceptorContext<string, string>(method, "localhost:5001", callOptions);

        BlocksContext.IsTestMode = true;
        BlocksContext.SetContext(CreateBlocksContext("tenant-1"), changeContext: true);

        try
        {
            ClientInterceptorContext<string, string>? capturedContext = null;

            var call = interceptor.AsyncUnaryCall(
                "ping",
                context,
                (req, ctx) =>
                {
                    capturedContext = ctx;
                    return CreateUnaryCall(Task.FromResult("pong"));
                });

            var result = await call.ResponseAsync;

            Assert.Equal("pong", result);
            Assert.True(capturedContext.HasValue);
            var actualContext = capturedContext.Value;
            Assert.NotNull(actualContext.Options.Headers);
            Assert.Equal("yes", actualContext.Options.Headers!.GetValue("existing"));
            Assert.Equal("tenant-1", actualContext.Options.Headers.GetValue(BlocksConstants.BlocksKey));
            Assert.Equal("hash-tenant-1", actualContext.Options.Headers.GetValue(BlocksConstants.BlocksGrpcKey));
            Assert.False(string.IsNullOrWhiteSpace(actualContext.Options.Headers.GetValue("traceparent")));

            var serializedContext = actualContext.Options.Headers.GetValue("SecurityContext");
            Assert.False(string.IsNullOrWhiteSpace(serializedContext));
            var deserializedContext = JsonSerializer.Deserialize<BlocksContext>(serializedContext!);
            Assert.NotNull(deserializedContext);
            Assert.Equal("tenant-1", deserializedContext!.TenantId);

            crypto.Verify(c => c.Hash("tenant-1", "salt-1"), Times.Once);
            tenants.Verify(t => t.GetTenantByID("tenant-1"), Times.Once);
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = false;
        }
    }

    [Fact]
    public async Task GrpcClientInterceptor_AsyncUnaryCall_ShouldHandleFaultedResponse_AndUseEmptySaltForMissingTenant()
    {
        var activitySource = new ActivitySource("test-grpc-client-fault");
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "test-grpc-client-fault",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);

        var crypto = new Mock<ICryptoService>();
        var tenants = new Mock<ITenants>();

        tenants.Setup(t => t.GetTenantByID("tenant-2")).Returns((Blocks.Genesis.Tenant?)null);
        crypto.Setup(c => c.Hash("tenant-2", string.Empty)).Returns("hash-tenant-2");

        var interceptor = new GrpcClientInterceptor(activitySource, crypto.Object, tenants.Object);
        var method = CreateStringMethod();
        var context = new ClientInterceptorContext<string, string>(method, null!, new CallOptions());

        BlocksContext.IsTestMode = true;
        BlocksContext.SetContext(CreateBlocksContext("tenant-2"), changeContext: true);

        try
        {
            ClientInterceptorContext<string, string>? capturedContext = null;
            var expectedException = new InvalidOperationException("grpc-failure");

            var call = interceptor.AsyncUnaryCall(
                "ping",
                context,
                (req, ctx) =>
                {
                    capturedContext = ctx;
                    return CreateUnaryCall(Task.FromException<string>(expectedException));
                });

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await call.ResponseAsync);
            Assert.Equal("grpc-failure", thrown.Message);

            Assert.True(capturedContext.HasValue);
            var actualContext = capturedContext.Value;
            Assert.NotNull(actualContext.Options.Headers);
            Assert.Equal("tenant-2", actualContext.Options.Headers!.GetValue(BlocksConstants.BlocksKey));
            Assert.Equal("hash-tenant-2", actualContext.Options.Headers.GetValue(BlocksConstants.BlocksGrpcKey));

            crypto.Verify(c => c.Hash("tenant-2", string.Empty), Times.Once);
            tenants.Verify(t => t.GetTenantByID("tenant-2"), Times.Once);
        }
        finally
        {
            BlocksContext.ClearContext();
            BlocksContext.IsTestMode = false;
        }
    }

    private static BlocksContext CreateBlocksContext(string tenantId)
    {
        return BlocksContext.Create(
            tenantId: tenantId,
            roles: new[] { "user" },
            userId: "user-1",
            isAuthenticated: true,
            requestUri: "/grpc",
            organizationId: "org-1",
            expireOn: DateTime.UtcNow.AddHours(1),
            email: "user@example.com",
            permissions: new[] { "read" },
            userName: "user",
            phoneNumber: "000",
            displayName: "User",
            oauthToken: "token",
            refreshToken: "refresh",
            actualTentId: tenantId);
    }

    private static Method<string, string> CreateStringMethod()
    {
        var marshaller = Marshallers.Create(
            value => Encoding.UTF8.GetBytes(value ?? string.Empty),
            data => Encoding.UTF8.GetString(data));

        return new Method<string, string>(MethodType.Unary, "test.Service", "Ping", marshaller, marshaller);
    }

    private static AsyncUnaryCall<string> CreateUnaryCall(Task<string> response)
    {
        return new AsyncUnaryCall<string>(
            response,
            Task.FromResult(new Metadata()),
            () => new Status(StatusCode.OK, string.Empty),
            () => new Metadata(),
            () => { });
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
