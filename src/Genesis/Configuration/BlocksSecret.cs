using Microsoft.Extensions.Configuration;
using Serilog;
using System.Reflection;

namespace Blocks.Genesis
{
    public sealed class BlocksSecret : IBlocksSecret
    {
        public string CacheConnectionString { get; set; } = string.Empty;
        public string MessageConnectionString { get; set; } = string.Empty;
        public string LogConnectionString { get; set; } = string.Empty;
        public string MetricConnectionString { get; set; } = string.Empty;
        public string TraceConnectionString { get; set; } = string.Empty;
        public string LogDatabaseName { get; set; } = string.Empty;
        public string MetricDatabaseName { get; set; } = string.Empty;
        public string TraceDatabaseName { get; set; } = string.Empty;
        public string ServiceName { get; set; } = string.Empty;
        public string DatabaseConnectionString { get ; set ; } = string.Empty;
        public string RootDatabaseName { get ; set ; } = string.Empty;
        public bool EnableHsts { get; set; }
        public string SshHost { get; set; } = string.Empty;
        public string SshUsername { get; set; } = string.Empty;
        public string SshPassword { get; set; } = string.Empty;
        public string SshNginxTemplate { get; set; } = string.Empty;
        public string ProdDatabaseConnectionString { get;set; } = string.Empty;
        public string LmtMessageConnectionString { get ; set ; } = string.Empty;
        public string LmtBlobStorageConnectionString { get ; set ; } = string.Empty;
        public string ProdVaultUrl { get ; set ; } = string.Empty;
        public string AllowedCorsOrigins { get; set; } = string.Empty;

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
                Log.Warning("Property {PropertyName} not found or is read-only.", propertyName);
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
                    Log.Warning(e, "Failed to convert secret value.");
                }
            }
            return value;
        }
    }
}
