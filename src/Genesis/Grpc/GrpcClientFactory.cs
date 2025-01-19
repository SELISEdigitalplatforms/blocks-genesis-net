using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using System.Diagnostics;

namespace Blocks.Genesis
{
    public class GrpcClientFactory
    {
        private readonly ActivitySource _activitySource;
        private readonly ICryptoService _cryptoService;
        private readonly ITenants _tenants;
        public GrpcClientFactory(ActivitySource activitySource, ICryptoService cryptoService, ITenants tenants)
        {
            _activitySource = activitySource;
            _cryptoService = cryptoService;
            _tenants = tenants;
        }

        public TClient CreateGrpcClient<TClient>(string address)
        where TClient : ClientBase<TClient>
        {
            var channel = GrpcChannel.ForAddress(address);
            var interceptor = new GrpcClientInterceptor(_activitySource, _cryptoService, _tenants);
            var interceptedChannel = channel.Intercept(interceptor);

            return (TClient)Activator.CreateInstance(typeof(TClient), interceptedChannel);
        }
    }
}
