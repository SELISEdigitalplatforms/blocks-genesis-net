namespace Blocks.Genesis
{
    public sealed class BlocksSecret : IBlocksSecret
    {
        public string CacheConnectionString { get; set; }
        public string MessageConnectionString { get; set; }
        public string LogConnectionString { get; set; }
        public string MetricConnectionString { get; set; }
        public string TraceConnectionString { get; set; }
        public string LogDatabaseName { get; set; }
        public string MetricDatabaseName { get; set; }
        public string TraceDatabaseName { get; set; }
        public string ServiceName { get; set; }
        public string DatabaseConnectionString { get ; set ; }
        public string ErrorVerbosity { get; set; } = "StackTrace";
        public string RooDatabaseName { get ; set ; }

        public static async Task<IBlocksSecret> ProcessBlocksSecret(CloudType cloudType, Dictionary<string, string> cloudConfig)
        {
            ICloudVault cloudVault = CloudVault.GetCloudVault(cloudType);
            var blocksSecret = await cloudVault.ProcessSecrets(new BlocksSecret(), cloudConfig);

            return blocksSecret;
        }

        public void UpdateProperty(string propertyName, object propertyValue)
        {
            var property = this.GetType().GetProperty(propertyName);

            if (property != null && property.CanWrite)
            {
                property.SetValue(this, propertyValue);
            }
            else
            {
                Console.WriteLine($"Property '{propertyName}' not found or is read-only.");
            }
        }


    }

}
