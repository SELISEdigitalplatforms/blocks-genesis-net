using SeliseBlocks.LMT.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Blocks.LMT.Client
{
    public interface ILmtMessageSender : IDisposable
    {
        Task SendLogsAsync(List<LogData> logs, int retryCount = 0);
        Task SendTracesAsync(Dictionary<string, List<TraceData>> tenantBatches, int retryCount = 0);
    }
}
