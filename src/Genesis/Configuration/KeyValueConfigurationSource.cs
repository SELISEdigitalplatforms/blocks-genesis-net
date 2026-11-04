using Microsoft.Extensions.Configuration;

namespace Blocks.Genesis;

public sealed class KeyValueConfigurationSource : IConfigurationSource
{
    private readonly KeyValueConfigurationOptions _options;

    public KeyValueConfigurationSource(KeyValueConfigurationOptions options)
    {
        _options = options;
    }

    public IConfigurationProvider Build(IConfigurationBuilder builder) => new KeyValueConfigurationProvider(_options);
}
