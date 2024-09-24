using Serilog;
using Serilog.Configuration;

namespace Blocks.Genesis
{
    public static class SerilogExtensions
    {
        public static LoggerConfiguration MongoDBWithDynamicCollection(this LoggerSinkConfiguration loggerConfiguration, string serviceName, IBlocksSecret blocksSecret)
        {
            return loggerConfiguration.Sink(new MongoDBDynamicSink(serviceName, blocksSecret), new BatchingOptions
            {
                //BufferingTimeLimit = TimeSpan.FromSeconds(3),
                BatchSizeLimit = 1000,
                EagerlyEmitFirstEvent = false
            });
        }
    }
}
