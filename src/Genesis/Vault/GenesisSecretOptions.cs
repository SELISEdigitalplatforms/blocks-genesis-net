namespace Blocks.Genesis
{
    public class GenesisSecretOptions
    {
        public SecretMode Mode { get; set; } = SecretMode.OnPrem;
        public AzureSecretOptions? Azure { get; set; }
        public PlatformOptions? Platform { get; set; }
    }

    public class AzureSecretOptions
    {
        public string VaultUri { get; set; } = string.Empty;
        public string? TenantId { get; set; }
        public string? ManagedIdentityClientId { get; set; }
    }

    public class PlatformOptions
    {
        public string BaseUrl { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string XBlocksKey { get; set; } = string.Empty;
    }
}
