using OpenTelemetry;
using Serilog.Core;
using Serilog.Events;
using System.Diagnostics;

namespace Blocks.Genesis
{
    public class TraceContextEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            var activity = Activity.Current;

            if (activity != null)
            {
                var tenantId = Baggage.GetBaggage("TenantId");
                tenantId = string.IsNullOrWhiteSpace(tenantId) ? BlocksConstants.Miscellaneous : tenantId;

                logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("TenantId", tenantId));
                logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("TraceId", activity?.TraceId));
                logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("SpanId", activity?.SpanId));
            }
        }
    }

}
