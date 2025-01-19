using Grpc.Core;
using Grpc.Core.Interceptors;
using System.Diagnostics;
using System.Text.Json;

namespace Blocks.Genesis
{
    public class GrpcClientInterceptor : Interceptor
    {
        private readonly ActivitySource _activitySource;
        private readonly ICryptoService _cryptoService;
        private readonly ITenants _tenants;

        public GrpcClientInterceptor(ActivitySource activitySource, ICryptoService cryptoService, ITenants tenants)
        {
            _activitySource = activitySource;
            _cryptoService = cryptoService;
            _tenants = tenants;
        }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            TRequest request,
            ClientInterceptorContext<TRequest, TResponse> context,
            AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
        {
            var securityContext = BlocksContext.GetContext();
            var tenant = _tenants.GetTenantByID(securityContext?.TenantId ?? string.Empty);

            using var activity = _activitySource.StartActivity("GrpcClientCall", ActivityKind.Producer, Activity.Current?.Context ?? default);
            var metadata = context.Options.Headers ?? new Metadata();
            metadata.Add("traceparent", activity?.Id ?? string.Empty);
            metadata.Add(BlocksConstants.BlocksKey, tenant?.ItemId ?? string.Empty);
            metadata.Add(BlocksConstants.BlocksGrpcKey, _cryptoService.Hash(tenant?.ItemId ?? string.Empty, tenant?.PasswordSalt ?? string.Empty));
            metadata.Add("SecurityContext", JsonSerializer.Serialize(securityContext));

            var newContext = new ClientInterceptorContext<TRequest, TResponse>(
                context.Method, context.Host, new CallOptions(metadata));

            return continuation(request, newContext);
        }
    }
}
