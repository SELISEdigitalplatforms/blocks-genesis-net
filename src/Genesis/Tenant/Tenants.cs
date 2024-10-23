using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using StackExchange.Redis;

namespace Blocks.Genesis
{
    public class Tenants : ITenants
    {
        private List<Tenant> _tenants = new List<Tenant>();
        private readonly ILogger<Tenants> _logger;
        private readonly IBlocksSecret _blocksSecret;
        private readonly ICacheClient _cacheClient;
        private readonly IMongoDatabase _database;


        public Tenants(ILogger<Tenants> logger, IBlocksSecret blocksSecret, ICacheClient cacheClient)
        {
            _logger = logger;
            _blocksSecret = blocksSecret;
            _cacheClient = cacheClient;

            CacheTenants();
        }

        public Tenant? GetTenantByID(string tenantId)
        {
            var tenant = _tenants.FirstOrDefault(t => t.ItemId == tenantId || t.TenantId == tenantId) ?? GetTenantFromCache(tenantId);

            return tenant;
        }

        public Dictionary<string, (string, string)> GetTenantDatabaseConnectionStrings()
        {
            return _tenants.ToDictionary(t => t.TenantId, t => (t.DBName, t.DbConnectionString));
        }

        public (string?, string?) GetTenantDatabaseConnectionString(string tenantId)
        {
            var tenant = _tenants?.FirstOrDefault(t => t.TenantId == tenantId);

            if (tenant == null)
            {
                tenant = GetTenantFromCache(tenantId);
            }

            return (tenant?.DBName, tenant?.DbConnectionString);
        }

        public JwtTokenParameters? GetTenantTokenValidationParameter(string tenantId)
        {
            var tenant = _tenants.FirstOrDefault((Tenant t) => t.TenantId.Equals(tenantId, StringComparison.InvariantCultureIgnoreCase));
            return tenant == null ? null : tenant.JwtTokenParameters;
        }

        public void CacheTenants()
        {
            try
            {
                IMongoDatabase _database = new MongoClient(_blocksSecret.DatabaseConnectionString).GetDatabase(_blocksSecret.RooDatabaseName);
                _tenants = _database.GetCollection<Tenant>(BlocksConstants.TenantCollectionName).Find(_ => true).ToList();

                foreach (var tenant in _tenants)
                {
                    SaveTenantInCache(tenant);
                    LmtConfiguration.CreateCollectionForTrace(_blocksSecret.TraceConnectionString, tenant.TenantId);
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, exception.Message);
            }


        }

        public void CacheTenant(string tenantId)
        {
            try
            {
                var tenant = _database.GetCollection<Tenant>(BlocksConstants.TenantCollectionName).Find((Tenant t) => t.ItemId == tenantId || t.TenantId == tenantId).FirstOrDefault();

                SaveTenantInCache(tenant);
                LmtConfiguration.CreateCollectionForTrace(_blocksSecret.TraceConnectionString, tenant.TenantId);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, exception.Message);
            }
        }


        private void SaveTenantInCache(Tenant tenant)
        {
            try
            {
                var hashEntries = new List<HashEntry>
                    {
                        new HashEntry("ItemId", tenant.ItemId),
                        new HashEntry("TenantId", tenant.TenantId),
                        new HashEntry("DBName", tenant.DBName),
                        new HashEntry("ApplicationDomain", tenant.ApplicationDomain),
                        new HashEntry("IsDisabled", tenant.IsDisabled),
                        new HashEntry("PasswordStrengthCheckerRegex", tenant.PasswordStrengthCheckerRegex?? ""),
                        new HashEntry("PasswordSalt", tenant.PasswordSalt ?? ""),
                        new HashEntry("DbConnectionString", tenant.DbConnectionString)
                    };

                _cacheClient.AddHashValue(BlocksConstants.TenantInfoCachePrefix + tenant.TenantId, hashEntries.ToArray());

                SaveTokenParametersInCache(tenant.TenantId, tenant.JwtTokenParameters);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, exception.Message);
            }
        }


        private void SaveTokenParametersInCache(string tenantId, JwtTokenParameters parameters)
        {
            try
            {
                var hashEntries = new List<HashEntry>
                {
                    new HashEntry("Issuer", parameters.Issuer),
                    new HashEntry("SigningKeyPath", parameters.SigningKeyPath),
                    new HashEntry("SigningKeyPassword", parameters.SigningKeyPassword)
                };

                foreach (var audience in parameters.Audiences)
                {
                    hashEntries.Add(new HashEntry($"Audience:{audience}", audience));
                }

                _cacheClient.AddHashValue(BlocksConstants.TenantTokenParametersCachePrefix + tenantId, hashEntries.ToArray());
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, exception.Message);
            }
        }

        private Tenant? GetTenantFromCache(string tenantId)
        {
            try
            {
                var hashEntries = _cacheClient.GetHashValue(BlocksConstants.TenantInfoCachePrefix + tenantId);

                if (hashEntries.Length == 0)
                {
                    throw new KeyNotFoundException("Tenant information is not found in Redis.");
                }

                var tenant = new Tenant
                {
                    ItemId = hashEntries.FirstOrDefault(e => e.Name == "ItemId").Value,
                    TenantId = hashEntries.FirstOrDefault(e => e.Name == "TenantId").Value,
                    DBName = hashEntries.FirstOrDefault(e => e.Name == "DBName").Value,
                    ApplicationDomain = hashEntries.FirstOrDefault(e => e.Name == "ApplicationDomain").Value,
                    IsDisabled = hashEntries.FirstOrDefault(e => e.Name == "IsDisabled").Value == "true",
                    DbConnectionString = hashEntries.FirstOrDefault(e => e.Name == "DbConnectionString").Value,
                    PasswordStrengthCheckerRegex = hashEntries.FirstOrDefault(e => e.Name == "PasswordStrengthCheckerRegex").Value,
                    PasswordSalt = hashEntries.FirstOrDefault(e => e.Name == "PasswordSalt").Value,
                    JwtTokenParameters = GetTokenParametersFromCache(tenantId),
                };

                _tenants.Add(tenant);

                return tenant;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, exception.Message);
                return null;
            }
        }

        private JwtTokenParameters GetTokenParametersFromCache(string tenantId)
        {
            try
            {
                var hashEntries = _cacheClient.GetHashValue(BlocksConstants.TenantTokenParametersCachePrefix + tenantId);

                if (hashEntries.Length == 0)
                {
                    throw new KeyNotFoundException("Tenant Token Parameters are not found in Redis.");
                }

                var tokenParameters = new JwtTokenParameters
                {
                    Issuer = hashEntries.FirstOrDefault(e => e.Name == "Issuer").Value,
                    Audiences = hashEntries.Where(e => e.Name.StartsWith("Audience:")).Select(e => (string)e.Value).ToList(),
                    SigningKeyPath = hashEntries.FirstOrDefault(e => e.Name == "SigningKeyPath").Value,
                    SigningKeyPassword = hashEntries.FirstOrDefault(e => e.Name == "SigningKeyPassword").Value
                };

                return tokenParameters;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, exception.Message);
                return new JwtTokenParameters();
            }
        }

    }
}
