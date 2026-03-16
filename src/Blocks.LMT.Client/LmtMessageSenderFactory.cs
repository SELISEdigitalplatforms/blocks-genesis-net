using SeliseBlocks.LMT.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Blocks.LMT.Client
{
    public static class LmtMessageSenderFactory
    {
        public static ILmtMessageSender Create(LmtOptions options)
        {
            if (LmtTransportHelper.IsRabbitMq(options.ConnectionString))
            {
                return new LmtRabbitMqSender(
                    options.ServiceId,
                    options.ConnectionString,
                    options.MaxRetries,
                    options.MaxFailedBatches);
            }

            return new LmtServiceBusSender(
                options.ServiceId,
                options.ConnectionString,
                options.MaxRetries,
                options.MaxFailedBatches);
        }
    }
}
