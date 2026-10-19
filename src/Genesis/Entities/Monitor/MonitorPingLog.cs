// Moved from blocks-monitor server/Alert.DomainService/Monitor/Entity/MonitorPingLog.cs (dev, 2026-10-19).
// Same class, collection and field names; no behaviour change. Run records written by the worker: no
// [SystemSchemaRules], access policies only.
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Monitor;

[BsonIgnoreExtraElements]
public class MonitorPingLog
{
    public string MonitorId { get; set; }
    public string Url { get; set; }
    public int StatusCode { get; set; }
    public string ResponseMessage { get; set; }
    public string ResponseBody { get; set; }
    public bool IsSuccess { get; set; }
    public double ResponseTimeMs { get; set; }
    public HttpMethodTypes? HttpMethodType { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
