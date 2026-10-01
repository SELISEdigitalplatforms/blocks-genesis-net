namespace Blocks.Genesis;

public static class BlocksConstants
{
    internal const string TenantCollectionName = "Tenants";
    internal const string TenantTokenPublicCertificateCachePrefix = "tetocertpublic::";

    /// <summary>
    /// Cache slot for one external provider's public certificate, keyed by tenant and provider.
    /// </summary>
    /// <remarks>
    /// Scoped per provider rather than per tenant, unlike
    /// <see cref="TenantTokenPublicCertificateCachePrefix"/>: a tenant may trust several external
    /// providers at once, and a tenant-wide slot would hand one provider's certificate to another.
    /// </remarks>
    internal const string ThirdPartyProviderCertificateCachePrefix = "tpprovcert::";
    internal const string ThirdPartyContextHeader = "ThirdPartyContext";
    public const string BlocksKey = "x-blocks-key";

    /// <summary>
    /// Names which external identity provider a token came from, for the one case issuer and
    /// audience cannot separate: two providers configured with both the same.
    /// </summary>
    public const string ThirdPartyIdpHeader = "x-blocks-idp";
    public const string BlocksGrpcKey = "x-blocks-service-key";
    public const string ProjectContextIdHeader = "x-context-id";
    public const string AuthorizationHeaderName = "Authorization";
    public const string Bearer = "Bearer ";
    public const string Miscellaneous = "miscellaneous";
    internal const string KeyVault = "KeyVault";
    internal const string ProtectedResourceName = "ProtectedResourceName";

}


