using System.Diagnostics;
using System.Text.Json;

namespace Blocks.Genesis
{
    public static class BlocksConstants
    {
        internal const string TenantCollectionName = "Tenants";
        internal const string TenantInfoCachePrefix = "teinfocache::";
        internal const string TenantTokenParametersCachePrefix = "tetoparams::";
        internal const string TenantTokenPublicCertificateCachePrefix = "tetocertpublic::";
        public const string BlocksKey = "X-Blocks-Key";
        public const string AuthorizationHeaderName = "Authorization";
        public const string Bearer = "Bearer ";
        public const string Miscellaneous = "miscellaneous";
        internal const string KeyVault = "KeyVault";

        private static void StoreTenantDataInActivity(Tenant tenant)
        {
            var activity = Activity.Current;
            if (activity != null)
            {
                var securityData = BlocksContext.CreateFromTuple((tenant.TenantId, Array.Empty<string>(), string.Empty, false, tenant.ApplicationDomain, string.Empty, DateTime.MinValue, string.Empty, Array.Empty<string>(), string.Empty));

                activity.SetCustomProperty("SecurityContext", JsonSerializer.Serialize(securityData));
            }
        }
    }
}
