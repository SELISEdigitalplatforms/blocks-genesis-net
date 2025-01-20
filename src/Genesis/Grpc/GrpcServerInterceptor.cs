using Grpc.Core;
using Grpc.Core.Interceptors;
using System.Diagnostics;

namespace Blocks.Genesis
{
    public class GrpcServerInterceptor : Interceptor
    {
        public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
            TRequest request,
            ServerCallContext context,
            UnaryServerMethod<TRequest, TResponse> continuation)
        {
            var securityContext = context.RequestHeaders.GetValue("SecurityContext");
            if (Activity.Current != null)
            {
                Activity.Current.SetCustomProperty("SecurityContext", securityContext);
            }

            return await continuation(request, context);
        }
    }
}
