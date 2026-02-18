using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Health
{
    [BsonIgnoreExtraElements]
    public class BlocksServicesHealthConfiguration
    {
        public string ServiceName { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public bool HealthCheckEnabled { get; set; }
        public int PingIntervalSeconds { get; set; }
    }
}