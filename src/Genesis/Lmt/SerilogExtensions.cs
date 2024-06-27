using Serilog;
using Serilog.Configuration;

namespace Blocks.Genesis
{
    public static class SerilogExtensions
    {
        public static LoggerConfiguration MongoDBWithDynamicCollection(this LoggerSinkConfiguration loggerConfiguration, string serviceName)
        {
            return loggerConfiguration.Sink(new MongoDBDynamicSink(serviceName), new BatchingOptions
            {
                BufferingTimeLimit = TimeSpan.FromSeconds(3),
                BatchSizeLimit = 1000,
                EagerlyEmitFirstEvent = false
            });
        }
    }
}
