using Microsoft.Extensions.Configuration;

namespace Blocks.Genesis;

public static class KeyValueConfigurationExtensions
{
    /// <summary>
    /// Loads the root database's configuration document for <paramref name="key"/> into
    /// <see cref="IConfiguration"/>: from <c>KeyValueStores</c>, or from the legacy <c>Secrets</c>
    /// collection when <c>KeyValueStores</c> has no document for that key.
    /// </summary>
    public static IConfigurationBuilder AddKeyValueConfiguration(
        this IConfigurationBuilder builder,
        IBlocksSecret blocksSecret,
        string key,
        bool optional = false)
    {
        ArgumentNullException.ThrowIfNull(blocksSecret);

        return builder.AddKeyValueConfiguration(options =>
        {
            options.ConnectionString = blocksSecret.DatabaseConnectionString;
            options.DatabaseName = blocksSecret.RootDatabaseName;
            options.Key = key;
            options.Optional = optional;
        });
    }

    public static IConfigurationBuilder AddKeyValueConfiguration(
        this IConfigurationBuilder builder,
        Action<KeyValueConfigurationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new KeyValueConfigurationOptions();
        configure(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new ArgumentException("[Genesis.KeyValueConfiguration] ConnectionString must not be empty.", nameof(configure));
        if (string.IsNullOrWhiteSpace(options.DatabaseName))
            throw new ArgumentException("[Genesis.KeyValueConfiguration] DatabaseName must not be empty.", nameof(configure));
        if (string.IsNullOrWhiteSpace(options.Key))
            throw new ArgumentException("[Genesis.KeyValueConfiguration] Key must not be empty.", nameof(configure));
        if (string.IsNullOrWhiteSpace(options.CollectionName))
            throw new ArgumentException("[Genesis.KeyValueConfiguration] CollectionName must not be empty.", nameof(configure));

        options.Key = options.Key.Trim();

        return builder.Add(new KeyValueConfigurationSource(options));
    }
}
