using Blocks.Genesis;
using Grpc.Core;
using Moq;
using System.Diagnostics;

namespace XUnitTest.Grpc;

public class GrpcClientFactoryScaffoldTests
{
    [Fact]
    public void Type_ShouldBeAccessible()
    {
        Assert.NotNull(typeof(GrpcClientFactory));
    }

    [Fact]
    public void CreateGrpcClient_ShouldReturnRequestedTypedClient()
    {
        var crypto = new Mock<ICryptoService>();
        var tenants = new Mock<ITenants>();

        var factory = new GrpcClientFactory(
            new ActivitySource("test-grpc"),
            crypto.Object,
            tenants.Object);

        var client = factory.CreateGrpcClient<FakeGrpcClient>("http://localhost:5001");

        Assert.NotNull(client);
        Assert.IsType<FakeGrpcClient>(client);
    }

    private sealed class FakeGrpcClient : ClientBase<FakeGrpcClient>
    {
        public FakeGrpcClient(CallInvoker callInvoker) : base(callInvoker)
        {
        }

        private FakeGrpcClient(ClientBaseConfiguration configuration) : base(configuration)
        {
        }

        protected override FakeGrpcClient NewInstance(ClientBaseConfiguration configuration)
            => new(configuration);
    }
}
