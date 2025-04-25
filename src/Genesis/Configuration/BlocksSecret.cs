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

        public static async Task<IBlocksSecret> ProcessBlocksSecret(VaultType vaultType = VaultType.Azure)
        {
            IVault cloudVault = Vault.GetCloudVault(vaultType);
            var blocksSecret = new BlocksSecret();
            PropertyInfo[] properties = typeof(BlocksSecret).GetProperties();
            var blocksSecretVault = await cloudVault.ProcessSecretsAsync(properties.Select(x => x.Name).ToList());

            foreach (PropertyInfo property in properties)
            {
                string propertyName = property.Name;
                var isExist = blocksSecretVault.TryGetValue(propertyName, out var retrievedValue);

                if (isExist && !string.IsNullOrWhiteSpace(retrievedValue))
                {
                    object convertedValue = ConvertValue(retrievedValue, property.PropertyType);

                    UpdateProperty(blocksSecret, propertyName, convertedValue);
                }
            }


            return blocksSecret;
        }

        

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
