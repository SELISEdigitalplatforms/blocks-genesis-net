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
                IMongoDatabase _database = new MongoClient(_blocksSecret.DatabaseConnectionString).GetDatabase(_blocksSecret.RootDatabaseName);
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
                var index = _tenants.FindIndex(x => x.TenantId == tenantId);
                if (index != -1) _tenants.RemoveAt(index);
                _tenants.Add(tenant);

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
                        new HashEntry("DbConnectionString", tenant.DbConnectionString),
                        new HashEntry("AccessTokenValidForNumberMinutes", tenant.AccessTokenValidForNumberMinutes),
                        new HashEntry("RefreshTokenValidForNumberMinutes", tenant.RefreshTokenValidForNumberMinutes),
                        new HashEntry("RememberMeRefreshTokenValidForNumberMinutes", tenant.RememberMeRefreshTokenValidForNumberMinutes),
                        new HashEntry("GetNumberOfWrongAttemptsToLockTheAccount", tenant.GetNumberOfWrongAttemptsToLockTheAccount),
                        new HashEntry("AccountLockDurationInMinutes", tenant.AccountLockDurationInMinutes),
                    };

                foreach (var grantType in tenant.AllowedGrantType)
                {
                    hashEntries.Add(new HashEntry($"AllowedGrantType:{grantType}", grantType));
                }

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
                    new HashEntry("PublicCertificatePath", parameters.PublicCertificatePath),
                    new HashEntry("PublicCertificatePassword", parameters.PublicCertificatePassword),
                    new HashEntry("PrivateCertificatePassword", parameters.PrivateCertificatePassword),
                    new HashEntry("CertificateValidForNumberOfDays", parameters.CertificateValidForNumberOfDays),
                    new HashEntry("IssueDate", parameters.IssueDate.ToString()),
                    
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
                    AllowedGrantType = hashEntries.Where(e => e.Name.StartsWith("AllowedGrantType:")).Select(e => (string)e.Value).ToList(),
                    JwtTokenParameters = GetTokenParametersFromCache(tenantId),
                    AccessTokenValidForNumberMinutes = (int)hashEntries.FirstOrDefault(e => e.Name == "AccessTokenValidForNumberMinutes").Value,
                    RefreshTokenValidForNumberMinutes = (int)hashEntries.FirstOrDefault(e => e.Name == "RefreshTokenValidForNumberMinutes").Value,
                    RememberMeRefreshTokenValidForNumberMinutes = (int)hashEntries.FirstOrDefault(e => e.Name == "RememberMeRefreshTokenValidForNumberMinute").Value,
                    GetNumberOfWrongAttemptsToLockTheAccount = (int)hashEntries.FirstOrDefault(e => e.Name == "GetNumberOfWrongAttemptsToLockTheAccount").Value,
                    AccountLockDurationInMinutes = (int)hashEntries.FirstOrDefault(e => e.Name == "AccountLockDurationInMinutes").Value,
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

        private JwtTokenParameters? GetTokenParametersFromCache(string tenantId)
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
                    PublicCertificatePath = hashEntries.FirstOrDefault(e => e.Name == "PublicCertificatePath").Value,
                    PublicCertificatePassword = hashEntries.FirstOrDefault(e => e.Name == "PublicCertificatePassword").Value,
                    PrivateCertificatePassword = hashEntries.FirstOrDefault(e => e.Name == "PrivateCertificatePassword").Value,
                    CertificateValidForNumberOfDays = (int)hashEntries.FirstOrDefault(e => e.Name == "CertificateValidForNumberOfDays").Value,
                    IssueDate = DateTime.Parse(hashEntries.FirstOrDefault(e => e.Name == "IssueDate").Value),
                };

                return tokenParameters;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, exception.Message);
                return null;
            }
        }

    }
}
