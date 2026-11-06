// Moved from blocks-monitor server/Alert.DomainService/Monitor/Models/IncidentListSummary.cs (dev, 2026-10-19).
// Same class and field names; no behaviour change.
namespace Blocks.Genesis.Entities.Monitor;

public class IncidentListSummary
{
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public double? DowntimeDurationSeconds { get; set; }
}
