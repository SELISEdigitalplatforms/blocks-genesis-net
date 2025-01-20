using Grpc.Core;

namespace Blocks.Genesis
{
    public interface IGrpcClientFactory
    {
        public TClient CreateGrpcClient<TClient>(string address) where TClient : ClientBase<TClient>;
    }
}
