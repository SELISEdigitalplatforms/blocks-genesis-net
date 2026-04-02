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

        public static string? GetLmtConnectionString()
        {
            var configuredConnectionString = _configuration?["Lmt:ConnectionString"];
            if (!string.IsNullOrWhiteSpace(configuredConnectionString))
                return configuredConnectionString;

            return Environment.GetEnvironmentVariable("Lmt__ConnectionString")
                ?? Environment.GetEnvironmentVariable("Lmt:ConnectionString");
        }

        public static int GetLmtMaxRetries()
        {
            var retries = _configuration?.GetSection("Lmt:MaxRetries")?.Value;
            if (int.TryParse(retries, out var retriesValue))
                return retriesValue;

            if (int.TryParse(Environment.GetEnvironmentVariable("MaxRetries"), out var envRetries))
                return envRetries;

            return 3;
        }

        public static int GetLmtMaxFailedBatches()
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