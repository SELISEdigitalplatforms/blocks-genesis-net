using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SeliseBlocks.LMT.Client
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

        public static bool IsValidConnectionString(string? connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                return false;

            // Check for RabbitMQ pattern
            if (IsRabbitMq(connectionString))
                return true;

            // Check for Service Bus pattern (should contain Endpoint and SharedAccessKey or similar)
            if (connectionString.Contains("Endpoint=", StringComparison.OrdinalIgnoreCase) &&
                (connectionString.Contains("SharedAccessKey=", StringComparison.OrdinalIgnoreCase) ||
                 connectionString.Contains("SharedAccessKeyValue=", StringComparison.OrdinalIgnoreCase)))
                return true;

            return false;
        }
    }
}
