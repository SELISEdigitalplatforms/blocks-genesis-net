using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Blocks.LMT.Client
{
    public static class LmtTransportHelper
    {
        public static bool IsRabbitMq(string? connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                return false;

            if (!Uri.TryCreate(connectionString, UriKind.Absolute, out var uri))
                return false;

            return uri.Scheme.Equals("amqp", StringComparison.OrdinalIgnoreCase) ||
                   uri.Scheme.Equals("amqps", StringComparison.OrdinalIgnoreCase);
        }
    }
}
