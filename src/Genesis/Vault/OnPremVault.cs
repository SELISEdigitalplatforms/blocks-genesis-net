using Microsoft.Extensions.Configuration;

namespace Blocks.Genesis
{
    public class LocalSecretProvider : ISecretProvider
    {
        private readonly IConfiguration _configuration;

        /// <summary>DI constructor — injects the application's IConfiguration.</summary>
        public LocalSecretProvider(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        /// <summary>No-arg constructor for use before the DI container is available (startup).</summary>
        public LocalSecretProvider()
        {
            _configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        }

        public Task<string?> GetAsync(string key)
        {
            var value = _configuration[$"BlocksSecret:{key}"] ?? _configuration[key];
            return Task.FromResult(value);
        }
    }
}