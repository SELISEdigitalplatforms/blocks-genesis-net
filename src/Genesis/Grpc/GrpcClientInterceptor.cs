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
            var tenantId = securityContext.TenantId;
            var tenant = _tenants.GetTenantByID(tenantId);

            var activity = _activitySource.StartActivity("GrpcClientCall", ActivityKind.Producer, Activity.Current?.Context ?? default);
            activity?.SetCustomProperty("TenantId", tenantId);
            activity?.SetTag("method", context.Method.ToString());
            activity?.SetTag("headers", context.Options.Headers?.ToString() ?? string.Empty);
            activity?.SetTag("deadline", context.Options.Deadline?.ToString() ?? string.Empty);
            activity?.SetTag("isWaitForReady", context.Options.IsWaitForReady);
            activity?.SetTag("host", context.Host ?? string.Empty);

            var metadata = context.Options.Headers ?? new Metadata();
            metadata.Add("traceparent", activity?.Id ?? string.Empty);
            metadata.Add(BlocksConstants.BlocksKey, tenantId);
            metadata.Add(BlocksConstants.BlocksGrpcKey, _cryptoService.Hash(tenantId, tenant?.PasswordSalt ?? string.Empty));
            metadata.Add("SecurityContext", JsonSerializer.Serialize(securityContext));

            var newContext = new ClientInterceptorContext<TRequest, TResponse>(
                context.Method, context.Host, new CallOptions(metadata));

            var call = continuation(request, newContext);

            call.ResponseAsync.ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, task.Exception?.Message);
                }
                else if (task.IsCompletedSuccessfully)
                {
                    activity?.SetStatus(ActivityStatusCode.Ok);
                }
                activity?.Stop();
                activity?.Dispose();
            });

            return call;
        }
    }
}
