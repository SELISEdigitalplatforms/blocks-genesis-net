namespace Blocks.Genesis
{
    public static class CloudVault
    {
        public static ICloudVault GetCloudVault(CloudType configType)
        {
            return configType switch
            {
                CloudType.Azure => new AzureKeyVault(),
                _ => throw new Exception("ConfigType is missing. Please see the Secret.json file")
            };
        }
    }
}
