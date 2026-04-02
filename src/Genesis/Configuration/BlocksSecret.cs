using Microsoft.Extensions.Configuration;
using System.Reflection;

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
        public string RootDatabaseName { get ; set ; }
        public bool EnableHsts { get; set; }
        public string SshHost { get; set; }
        public string SshUsername { get; set; }
        public string SshPassword { get; set; }
        public string SshNginxTemplate { get; set; }
        public string ProdDatabaseConnectionString { get;set; }
        public string LmtMessageConnectionString { get ; set ; }
        public string LmtBlobStorageConnectionString { get ; set ; }
        public string ProdVaultUrl { get ; set ; }
        public string ProdVaultTenantId { get ; set ; }
        public string ProdVaultClientId { get ; set ; }
        public string ProdVaultClientSecret { get ; set ; }

        public static async Task<IBlocksSecret> ProcessBlocksSecret(GenesisSecretOptions options)
        {
            ISecretProvider provider = CreateProvider(options);
            var blocksSecret = new BlocksSecret();
            PropertyInfo[] properties = typeof(BlocksSecret).GetProperties();

            foreach (PropertyInfo property in properties)
            {
                var value = await provider.GetAsync(property.Name);

                if (!string.IsNullOrWhiteSpace(value))
                {
                    object convertedValue = ConvertValue(value, property.PropertyType);
                    UpdateProperty(blocksSecret, property.Name, convertedValue);
                }
            }

            return blocksSecret;
        }

        private static ISecretProvider CreateProvider(GenesisSecretOptions options) => options.Mode switch
        {
            SecretMode.Azure => new AzureSecretProvider(),
            SecretMode.OnPrem => new LocalSecretProvider(),
            SecretMode.Platform => new PlatformSecretProvider(new HttpClient(), options),
            _ => throw new InvalidOperationException($"Unknown SecretMode: {options.Mode}")
        };

        

        public static void UpdateProperty<T>(T blocksSecret, string propertyName, object propertyValue) where T : class
        {
            var property = blocksSecret.GetType().GetProperty(propertyName);

            if (property != null && property.CanWrite)
            {
                property.SetValue(blocksSecret, propertyValue);
            }
            else
            {
                Console.WriteLine($"Property '{propertyName}' not found or is read-only.");
            }
        }

        public static object ConvertValue(string value, Type targetType)
        {
            if (targetType != typeof(string))
            {
                try
                {
                    return Convert.ChangeType(value, targetType);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                }
            }
            return value;
        }
    }
}
