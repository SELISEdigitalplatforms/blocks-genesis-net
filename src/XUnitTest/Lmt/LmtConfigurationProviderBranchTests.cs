using Microsoft.Extensions.Configuration;
using System.Reflection;

namespace XUnitTest.Lmt;

[Collection("Test collection for XUnitTest.Lmt.LmtConfigurationProviderTests")]
public class LmtConfigurationProviderBranchTests
{
    [Fact]
    public void GetMaxRetriesAndBatches_ShouldPreferConfigurationValues_WhenInitialized()
    {
        var type = Type.GetType("Blocks.Genesis.LmtConfigurationProvider, Blocks.Genesis");
        Assert.NotNull(type);
        var configField = type!.GetField("_configuration", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(configField);
        var previous = configField!.GetValue(null);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Lmt:MaxRetries"] = "7",
                    ["Lmt:MaxFailedBatches"] = "9"
                })
                .Build();

            type.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [configuration]);

            var retries = (int)type.GetMethod("GetMaxRetries", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [])!;
            var batches = (int)type.GetMethod("GetMaxFailedBatches", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [])!;

            Assert.Equal(7, retries);
            Assert.Equal(9, batches);
        }
        finally
        {
            configField.SetValue(null, previous);
        }
    }

    [Fact]
    public void GetMaxRetriesAndBatches_ShouldFallBackToDefaults_WhenConfigurationHasNoValues()
    {
        var type = Type.GetType("Blocks.Genesis.LmtConfigurationProvider, Blocks.Genesis");
        Assert.NotNull(type);
        var configField = type!.GetField("_configuration", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(configField);
        var previous = configField!.GetValue(null);
        var previousRetries = Environment.GetEnvironmentVariable("MaxRetries");
        var previousBatches = Environment.GetEnvironmentVariable("MaxFailedBatches");
        try
        {
            configField.SetValue(null, new ConfigurationBuilder().Build());
            Environment.SetEnvironmentVariable("MaxRetries", null);
            Environment.SetEnvironmentVariable("MaxFailedBatches", null);

            var retries = (int)type.GetMethod("GetMaxRetries", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [])!;
            var batches = (int)type.GetMethod("GetMaxFailedBatches", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [])!;

            Assert.Equal(3, retries);
            Assert.Equal(100, batches);
        }
        finally
        {
            configField.SetValue(null, previous);
            Environment.SetEnvironmentVariable("MaxRetries", previousRetries);
            Environment.SetEnvironmentVariable("MaxFailedBatches", previousBatches);
        }
    }
}
