using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Health
{
    [BsonIgnoreExtraElements]
    public class BlocksServicesHealthConfiguration : IBlocksServicesHealthConfiguration
    {
        public string ServiceName { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public bool HealthCheckEnabled { get; set; }
        public int PingIntervalSeconds { get; set; }
    }
}
