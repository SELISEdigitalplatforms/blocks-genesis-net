using StackExchange.Redis;
using System.Security.Cryptography.X509Certificates;

namespace Blocks.Genesis
{
    public class JwtValidationService : IJwtValidationService
    {
        private readonly IDatabase _redisDb;
        private readonly string _tenantPrefix = "tenanttokenparameters:";

        public JwtValidationService(ICacheClient cacheClient)
        {
            _redisDb = cacheClient.CacheDatabase();
        }

        public JwtTokenParameters GetTokenParameters(string tenantId)
        {
            var hashEntries = _redisDb.HashGetAll(_tenantPrefix + tenantId);

            if (hashEntries.Length == 0)
            {
                throw new KeyNotFoundException("Tenant information not found in Redis.");
            }

            var tokenParameters = new JwtTokenParameters
            {
                Issuer = hashEntries.FirstOrDefault(e => e.Name == "issuer").Value,
                Audiences = hashEntries.Where(e => e.Name.StartsWith("audience:")).Select(e => (string)e.Value).ToList(),
                SigningKeyPath = hashEntries.FirstOrDefault(e => e.Name == "signingKeyPath").Value,
                SigningKeyPassword = hashEntries.FirstOrDefault(e => e.Name == "signingKeyPassword").Value
            };

            return tokenParameters;
        }

        public void SaveTokenParameters(string tenantId, JwtTokenParameters parameters)
        {
            var hashEntries = new List<HashEntry>
            {
                new HashEntry("issuer", parameters.Issuer),
                new HashEntry("signingKeyPath", parameters.SigningKeyPath),
                new HashEntry("signingKeyPassword", parameters.SigningKeyPassword)
            };

            foreach (var audience in parameters.Audiences)
            {
                hashEntries.Add(new HashEntry($"audience:{audience}", audience));
            }

            _redisDb.HashSet(_tenantPrefix + tenantId, hashEntries.ToArray());
        }

        public static X509Certificate2 CreateSecurityKey(string signingKeyPath, string signingKeyPassword)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(signingKeyPassword))
                {
                    return new X509Certificate2(signingKeyPath);
                }
                else
                {
                    return new X509Certificate2(signingKeyPath, signingKeyPassword);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error creating security key: {e.Message}");
                return null;
            }
        }


    }
}
