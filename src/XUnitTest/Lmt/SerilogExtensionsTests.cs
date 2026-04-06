using Blocks.Genesis;
using Moq;
using Serilog;

namespace XUnitTest.Lmt;

public class SerilogExtensionsTests
{
    [Fact]
    public void MongoDBWithDynamicCollection_ShouldReturnLoggerConfiguration()
    {
        var previous = Environment.GetEnvironmentVariable("ServiceBusConnectionString");
        try
        {
            Environment.SetEnvironmentVariable("ServiceBusConnectionString", null);

            var blocksSecret = new Mock<IBlocksSecret>();
            blocksSecret.SetupGet(s => s.LogConnectionString).Returns(string.Empty);

            var config = new LoggerConfiguration();
            var result = config.WriteTo.MongoDBWithDynamicCollection("svc-serilog", blocksSecret.Object);

            Assert.NotNull(result);

            using var logger = result.CreateLogger();
            logger.Information("serilog extension smoke test");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ServiceBusConnectionString", previous);
        }
    }
}
