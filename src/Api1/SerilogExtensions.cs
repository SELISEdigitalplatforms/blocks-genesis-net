using Serilog.Configuration;
using Serilog.Events;
using Serilog;

namespace Api1
{
    public static class SerilogExtensions
    {
        public static LoggerConfiguration MongoDBWithDynamicCollection(this LoggerSinkConfiguration loggerConfiguration)
        {
            return loggerConfiguration.Sink(new MongoDBDynamicSink());
        }
    }
}
