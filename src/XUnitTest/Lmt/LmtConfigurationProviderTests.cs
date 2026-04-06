using Blocks.Genesis;
using Microsoft.Extensions.Configuration;
using System.Reflection;

namespace XUnitTest.Lmt;

public class LmtConfigurationProviderTests
{
    [Fact]
    public void GetServiceBusConnectionString_ShouldReturnEnvironmentValue()
    {
        var previous = Environment.GetEnvironmentVariable("ServiceBusConnectionString");
        try
        {
            Environment.SetEnvironmentVariable("ServiceBusConnectionString", "Endpoint=sb://x/;SharedAccessKeyName=k;SharedAccessKey=s");

            var value = (string?)InvokeProviderMethod("GetServiceBusConnectionString");

            Assert.Contains("Endpoint=sb://x/", value);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ServiceBusConnectionString", previous);
        }
    }

    [Fact]
    public void GetMaxRetries_ShouldPreferConfigurationThenEnvThenDefault()
    {
        var previous = Environment.GetEnvironmentVariable("MaxRetries");
        try
        {
            InitializeProvider(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Lmt:MaxRetries"] = "9"
            }).Build());
            Environment.SetEnvironmentVariable("MaxRetries", "8");
            Assert.Equal(9, (int)InvokeProviderMethod("GetMaxRetries")!);

            InitializeProvider(new ConfigurationBuilder().AddInMemoryCollection().Build());
            Assert.Equal(8, (int)InvokeProviderMethod("GetMaxRetries")!);

            Environment.SetEnvironmentVariable("MaxRetries", "not-int");
            Assert.Equal(3, (int)InvokeProviderMethod("GetMaxRetries")!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MaxRetries", previous);
        }
    }

    [Fact]
    public void GetMaxFailedBatches_ShouldPreferConfigurationThenEnvThenDefault()
    {
        var previous = Environment.GetEnvironmentVariable("MaxFailedBatches");
        try
        {
            InitializeProvider(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Lmt:MaxFailedBatches"] = "77"
            }).Build());
            Environment.SetEnvironmentVariable("MaxFailedBatches", "66");
            Assert.Equal(77, (int)InvokeProviderMethod("GetMaxFailedBatches")!);

            InitializeProvider(new ConfigurationBuilder().AddInMemoryCollection().Build());
            Assert.Equal(66, (int)InvokeProviderMethod("GetMaxFailedBatches")!);

            Environment.SetEnvironmentVariable("MaxFailedBatches", "invalid");
            Assert.Equal(100, (int)InvokeProviderMethod("GetMaxFailedBatches")!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MaxFailedBatches", previous);
        }
    }

    private static void InitializeProvider(IConfiguration configuration)
    {
        var type = typeof(ApplicationConfigurations).Assembly.GetType("Blocks.Genesis.LmtConfigurationProvider")!;
        var method = type.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)!;
        method.Invoke(null, new object[] { configuration });
    }

    private static object? InvokeProviderMethod(string name)
    {
        var type = typeof(ApplicationConfigurations).Assembly.GetType("Blocks.Genesis.LmtConfigurationProvider")!;
        var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;
        return method.Invoke(null, null);
    }
}
