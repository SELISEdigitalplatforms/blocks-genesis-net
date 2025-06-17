using Blocks.Genesis;
using Grpc.Core;
using System.Text.Json;

namespace GrpcServiceTestTemp.Services
{
    public class GreeterService : Greeter.GreeterBase
    {
        private readonly ILogger<GreeterService> _logger;
        public GreeterService(ILogger<GreeterService> logger)
        {
            _logger = logger;
        }

        public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context)
        {
            var sb = BlocksContext.GetContext();
            return Task.FromResult(new HelloReply
            {
                Message = JsonSerializer.Serialize(sb)
            });
        }
    }
}
