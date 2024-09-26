using Blocks.Genesis;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

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

        public S1Controller(ILogger<S1Controller> logger, IHttpService httpService, IMessageClient messageClient, IDbContextProvider dbContextProvider)
        {
            _logger = logger;
            _httpService = httpService;
            _messageClient = messageClient;
            _dbContextProvider = dbContextProvider;
        }

        [HttpGet("process")]
        public async Task<IActionResult> ProcessRequest()
        {
            _logger.LogInformation("Processing request in S1");

            var sc = BlocksContext.GetContext();

            // Send event to B1
            await Task.WhenAll(
                _messageClient.SendToConsumerAsync(new ConsumerMessage<W2Context> { ConsumerName = "demo_queue", Payload = new W2Context { Data = "From S2" } }),
            _messageClient.SendToMassConsumerAsync(new ConsumerMessage<W1Context> { ConsumerName = "demo_topic", Payload = new W1Context { Data = "From S1" } })
            , CallApi()
            );
            _logger.LogInformation("S1 send an event to B1");

            var collection = _dbContextProvider.GetCollection<W2Context>("W2Context");
            Func<Task<List<W2Context>>> command = async () => await collection.Find(_ => true).ToListAsync();

            var result = await _dbContextProvider.RunMongoCommandWithActivityAsync("W2Context", "Find", command);

            return Ok(result);
        }

        private async Task CallApi()
        {
            // Make HTTP call to S2
            var response = await _httpService.MakeGetRequest<object>("http://localhost:51846/api/s2/process", 
                new Dictionary<string, string> { { BlocksConstants.BlocksKey, "***REMOVED***" } });

            var collection = _dbContextProvider.GetCollection<W2Context>("W2Context");
            Func<bool> command = () =>
            {
                collection.InsertOne(new W2Context { Data = "Test", Id = Guid.NewGuid().ToString()});
                return true;
            };
            var result = _dbContextProvider.RunMongoCommandWithActivity("W2Context", "Insert", command);

            _logger.LogInformation("S1 call to S2 {r}", result);
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

}
