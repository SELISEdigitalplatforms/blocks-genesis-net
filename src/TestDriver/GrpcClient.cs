using Blocks.Genesis;

namespace TestDriver
{
    public class GrpcClient : IGrpcClient
    {
        private readonly IGrpcClientFactory _grpcClientFactory;

        public GrpcClient(IGrpcClientFactory grpcClientFactory)
        {
            _grpcClientFactory = grpcClientFactory;
        }

        public async Task<HelloReply?> ExecuteAsync()
        {
            try
            {
                var client = _grpcClientFactory.CreateGrpcClient<Greeter.GreeterClient>("http://localhost:3001");

                var reply = await client.SayHelloAsync(new HelloRequest { Name = "GreeterClient" });
                Console.WriteLine(reply);
                return reply;
            }
            catch (Exception e)
            {

                Console.WriteLine(e);
                return null;
            }
        }
    }
}
