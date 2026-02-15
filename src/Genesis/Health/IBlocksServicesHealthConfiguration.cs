using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Blocks.Genesis.Health
{
    public interface IBlocksServicesHealthConfiguration
    {
        string ServiceName { get; set; }
        string Endpoint { get; set; }
        bool HealthCheckEnabled { get; set; }
        int PingIntervalSeconds { get; set; }
    }
}
