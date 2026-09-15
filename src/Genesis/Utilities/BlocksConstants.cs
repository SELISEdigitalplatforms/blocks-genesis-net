namespace Blocks.Genesis;

public static class BlocksConstants
{
    internal const string TenantCollectionName = "Tenants";
    internal const string TenantTokenPublicCertificateCachePrefix = "tetocertpublic::";
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


