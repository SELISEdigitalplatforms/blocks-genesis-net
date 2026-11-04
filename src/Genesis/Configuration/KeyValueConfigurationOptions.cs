namespace Blocks.Genesis;

/// <summary>
/// Options for <see cref="KeyValueConfigurationExtensions.AddKeyValueConfiguration(Microsoft.Extensions.Configuration.IConfigurationBuilder, Action{KeyValueConfigurationOptions})"/>.
/// </summary>
public sealed class KeyValueConfigurationOptions
{
    /// <summary>Legacy collection read when the key is not in <see cref="CollectionName"/>.</summary>
    public const string DefaultFallbackCollectionName = "Secrets";

    public string ConnectionString { get; set; } = string.Empty;

    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>
    /// The configuration document's key, e.g. <c>blocks-secret-localization</c>. Matched against
    /// <c>Key</c> in <see cref="CollectionName"/> and against <c>SecretKey</c> in
    /// <see cref="FallbackCollectionName"/>.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    public string CollectionName { get; set; } = MongoKeyValueStore.CollectionName;

    /// <summary>
    /// Read only when <see cref="CollectionName"/> holds no document for <see cref="Key"/>. Set to
    /// <c>null</c> or empty to disable the fallback.
    /// </summary>
    public string? FallbackCollectionName { get; set; } = DefaultFallbackCollectionName;

    /// <summary>
    /// When true, a key found in neither collection, or an unreachable database, yields no values
    /// instead of failing startup.
    /// </summary>
    public bool Optional { get; set; }
}
