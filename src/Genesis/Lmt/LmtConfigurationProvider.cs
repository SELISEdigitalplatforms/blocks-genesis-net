using Microsoft.Extensions.Configuration;

namespace Blocks.Genesis
{
    internal static class LmtConfigurationProvider
    {
        private static IConfiguration? _configuration;

        public static void Initialize(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public static string? GetLogsServiceBusConnectionString()
        {
            var connString = _configuration?.GetSection("Lmt:LogsServiceBusConnectionString")?.Value;

            return connString ?? Environment.GetEnvironmentVariable("LogsServiceBusConnectionString");
        }

        public static string? GetTracesServiceBusConnectionString()
        {
            var connString = _configuration?.GetSection("Lmt:TracesServiceBusConnectionString")?.Value;

            return connString ?? Environment.GetEnvironmentVariable("TracesServiceBusConnectionString");
        }

        public static int GetMaxRetries()
        {
            var retries = _configuration?.GetSection("Lmt:MaxRetries")?.Value;
            if (int.TryParse(retries, out var retriesValue))
                return retriesValue;

            if (int.TryParse(Environment.GetEnvironmentVariable("MaxRetries"), out var envRetries))
                return envRetries;

            return 3;
        }

        public static int GetMaxFailedBatches()
        {
            var batches = _configuration?.GetSection("Lmt:MaxFailedBatches")?.Value;
            if (int.TryParse(batches, out var batchesValue))
                return batchesValue;

            if (int.TryParse(Environment.GetEnvironmentVariable("MaxFailedBatches"), out var envBatches))
                return envBatches;

            return 100;
        }
    }
}