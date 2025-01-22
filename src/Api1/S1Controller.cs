using Blocks.Genesis;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using TestDriver;

namespace ApiOne
{
    [ApiController]
    [Route("api/[controller]")]
    public class S1Controller : ControllerBase
    {
        private readonly ILogger<S1Controller> _logger;
        private readonly IHttpService _httpService;
        private readonly IMessageClient _messageClient;
        private readonly IDbContextProvider _dbContextProvider;
        private readonly IGrpcClient _grpcClient;
        private readonly ChangeControllerContext _changeControllerContext;

        public S1Controller(ILogger<S1Controller> logger, IHttpService httpService, 
            IMessageClient messageClient, IDbContextProvider dbContextProvider, 
            IGrpcClient grpcClient, ChangeControllerContext changeControllerContext)
        {
            _logger = logger;
            _httpService = httpService;
            _messageClient = messageClient;
            _dbContextProvider = dbContextProvider;
            _grpcClient = grpcClient;
            _changeControllerContext = changeControllerContext;
        }

        [HttpGet("process")]
        public async Task<IActionResult> ProcessRequest([FromQuery] ProcessRequest request)
        {
            var sc = BlocksContext.GetContext();
            Console.WriteLine(sc);

            _changeControllerContext.ChangeContext(request);
            _logger.LogInformation("Processing request in S1");
            // Send event to B1
            await Task.WhenAll(
                _messageClient.SendToConsumerAsync(new ConsumerMessage<W2Context> { ConsumerName = "demo_queue", Payload = new W2Context { Data = "From S2" } }),
            _messageClient.SendToMassConsumerAsync(new ConsumerMessage<W1Context> { ConsumerName = "demo_topic", Payload = new W1Context { Data = "From S1" } })
            , CallApi()
            );
            _logger.LogInformation("S1 send an event to B1");

            var collection = _dbContextProvider.GetCollection<W2Context>("W2Context");
            var result = await collection.Find(_ => true).ToListAsync();

            var grpc = await _grpcClient.ExecuteAsync();

            return Ok(new {http = result, grpc });
        }

        private async Task CallApi()
        {
            // Make HTTP call to S2
            var sc = BlocksContext.GetContext();
            Console.WriteLine(sc);
            var response = await _httpService.Get<object>("http://localhost:51846/api/s2/process",
                new Dictionary<string, string> { { BlocksConstants.BlocksKey, sc.TenantId } });

            //var collection = _dbContextProvider.GetCollection<W2Context>("W2Context");

            //collection.InsertOne(new W2Context { Data = "Test", Id = Guid.NewGuid().ToString() });

            _logger.LogInformation("S1 call to S2 {r}", true);
        }

        [HttpGet("cert")]
        [ProtectedEndPoint]
        public async Task<IActionResult> CertRequest()
        {
            _logger.LogInformation("Processing request in S1");

            var sc = BlocksContext.GetContext();

            //ICloudVault cloudVault = CloudVault.GetCloudVault(CloudType.Azure);
            //var blocksSecretVault = await cloudVault.ProcessCertificateAsync("***REMOVED***", GetVaultConfig());

            return Ok(sc);
        }

        private static Dictionary<string, string> GetVaultConfig()
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            var keyVaultConfig = new Dictionary<string, string>();
            configuration.GetSection("KeyVault").Bind(keyVaultConfig);

            return keyVaultConfig;
        }
    }

    public record W2Context
    {
        [BsonId]
        public string Id { get; set; }
        public string Data { get; set; }
    }

    public record W1Context
    {
        public string Data { get; set; }
    }

    public record ProcessRequest : IProjectKey
    {
        public string? ProjectKey { get; set; }
    }

}
