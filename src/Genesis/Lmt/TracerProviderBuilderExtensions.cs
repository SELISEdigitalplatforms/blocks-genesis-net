using OpenTelemetry.Trace;

namespace Blocks.Genesis
{
    public static class TracerProviderBuilderExtensions
    {
        public static TracerProviderBuilder AddMongoDBInstrumentation(this TracerProviderBuilder builder)
            => builder.AddSource("MongoDB.Driver.Core.Extensions.DiagnosticSources");
    }
}