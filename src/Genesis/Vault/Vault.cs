namespace Blocks.Genesis
{
    public static class Vault
    {
        public static IVault GetCloudVault(VaultType configType)
        {
            return configType switch
            {
                VaultType.Azure => new AzureKeyVault(),
                VaultType.OnPrem => new OnPremVault(),
                _ => throw new Exception("ConfigType is missing. Please see the Secret.json file")
            };
        }
    }
}
