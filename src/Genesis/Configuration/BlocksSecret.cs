using Microsoft.Extensions.Configuration;

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

            foreach (var binding in GetBindings())
            {
                var value = await provider.GetAsync(binding.Key);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                binding.Assign(blocksSecret, ConvertConfiguredValue(binding.Key, value, binding.ValueType));
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

        private static IReadOnlyList<SecretBinding> GetBindings()
        {
            return
            [
                new(nameof(CacheConnectionString), typeof(string), static (secret, value) => secret.CacheConnectionString = (string)value),
                new(nameof(MessageConnectionString), typeof(string), static (secret, value) => secret.MessageConnectionString = (string)value),
                new(nameof(LogConnectionString), typeof(string), static (secret, value) => secret.LogConnectionString = (string)value),
                new(nameof(MetricConnectionString), typeof(string), static (secret, value) => secret.MetricConnectionString = (string)value),
                new(nameof(TraceConnectionString), typeof(string), static (secret, value) => secret.TraceConnectionString = (string)value),
                new(nameof(LogDatabaseName), typeof(string), static (secret, value) => secret.LogDatabaseName = (string)value),
                new(nameof(MetricDatabaseName), typeof(string), static (secret, value) => secret.MetricDatabaseName = (string)value),
                new(nameof(TraceDatabaseName), typeof(string), static (secret, value) => secret.TraceDatabaseName = (string)value),
                new(nameof(ServiceName), typeof(string), static (secret, value) => secret.ServiceName = (string)value),
                new(nameof(DatabaseConnectionString), typeof(string), static (secret, value) => secret.DatabaseConnectionString = (string)value),
                new(nameof(RootDatabaseName), typeof(string), static (secret, value) => secret.RootDatabaseName = (string)value),
                new(nameof(EnableHsts), typeof(bool), static (secret, value) => secret.EnableHsts = (bool)value),
                new(nameof(SshHost), typeof(string), static (secret, value) => secret.SshHost = (string)value),
                new(nameof(SshUsername), typeof(string), static (secret, value) => secret.SshUsername = (string)value),
                new(nameof(SshPassword), typeof(string), static (secret, value) => secret.SshPassword = (string)value),
                new(nameof(SshNginxTemplate), typeof(string), static (secret, value) => secret.SshNginxTemplate = (string)value),
                new(nameof(ProdDatabaseConnectionString), typeof(string), static (secret, value) => secret.ProdDatabaseConnectionString = (string)value),
                new(nameof(LmtMessageConnectionString), typeof(string), static (secret, value) => secret.LmtMessageConnectionString = (string)value),
                new(nameof(LmtBlobStorageConnectionString), typeof(string), static (secret, value) => secret.LmtBlobStorageConnectionString = (string)value),
                new(nameof(ProdVaultUrl), typeof(string), static (secret, value) => secret.ProdVaultUrl = (string)value),
                new(nameof(ProdVaultTenantId), typeof(string), static (secret, value) => secret.ProdVaultTenantId = (string)value),
                new(nameof(ProdVaultClientId), typeof(string), static (secret, value) => secret.ProdVaultClientId = (string)value),
                new(nameof(ProdVaultClientSecret), typeof(string), static (secret, value) => secret.ProdVaultClientSecret = (string)value)
            ];
        }

        private static object ConvertConfiguredValue(string key, string value, Type targetType)
        {
            try
            {
                return targetType == typeof(string)
                    ? value
                    : Convert.ChangeType(value, targetType);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"Invalid secret value for '{key}'.", exception);
            }
        }

        public static void UpdateProperty<T>(T blocksSecret, string propertyName, object propertyValue) where T : class
        {
            var property = blocksSecret.GetType().GetProperty(propertyName);

            if (property != null && property.CanWrite)
            {
                property.SetValue(blocksSecret, propertyValue);
            }
        }

        public static object ConvertValue(string value, Type targetType)
        {
            if (targetType == typeof(string))
            {
                return value;
            }

            try
            {
                return Convert.ChangeType(value, targetType);
            }
            catch
            {
                return value;
            }
        }

        private sealed record SecretBinding(string Key, Type ValueType, Action<BlocksSecret, object> Assign);
    }
}
