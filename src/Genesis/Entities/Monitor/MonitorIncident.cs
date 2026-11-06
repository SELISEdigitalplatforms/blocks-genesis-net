// Moved from blocks-monitor server/Alert.DomainService/Monitor/Entity/MonitorIncident.cs (dev, 2026-10-19).
// Same class, collection and field names; no behaviour change. Written by the worker and the anonymous
// inbound ping, never by a signed-in user, so it has no [SystemSchemaRules]: access policies only.
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Monitor;

[BsonIgnoreExtraElements]
public class MonitorIncident : BaseEntity
{
    public string MonitorId { get; set; }
    public string MonitorName { get; set; }
    public string MonitorUrl { get; set; }
    public string ProjectKey { get; set; }
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public DateTime? EndTime { get; set; }

    public MonitorConfigurationTypes? MonitorConfigurationType { get; set; }

    public MonitorSourceTypes? MonitorSourceType { get; set; }

    public int LastStatusCode { get; set; }
    public bool IsResolved { get; set; } = false;

    public string FailureReason { get; set; }
    public string ResponseBody { get; set; }
    public double? DowntimeDurationSeconds =>
        IsResolved && EndTime.HasValue
            ? (EndTime.Value - StartTime).TotalSeconds
            : null;
}
