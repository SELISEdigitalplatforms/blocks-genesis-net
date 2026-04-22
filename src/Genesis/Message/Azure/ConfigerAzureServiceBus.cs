namespace Blocks.Genesis;

[Obsolete("Use ConfigureAzureServiceBus instead. This shim will be removed in the next major version.")]
public static class ConfigerAzureServiceBus
{
    [Obsolete("Use ConfigureAzureServiceBus.ConfigureQueueAndTopicAsync instead.")]
    public static Task ConfigerQueueAndTopicAsync(MessageConfiguration messageConfiguration)
        => ConfigureAzureServiceBus.ConfigureQueueAndTopicAsync(messageConfiguration);
}
