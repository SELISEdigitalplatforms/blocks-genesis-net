// Moved from blocks-monitor server/Alert.DomainService/Monitor/Entity/MonitorConfiguration.cs (dev, 2026-10-19).
// Same class, collection and field names; no behaviour change.
using Blocks.Genesis.DataAccess.Rules;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Monitor;

[BsonIgnoreExtraElements]
[SystemSchemaRules(RuleLevel.Normal)]
public class MonitorConfiguration : BaseEntity
{
    public string? TenantId { get; set; }
    public string? ExternalServiceId { get; set; }
    public string? ExternalServiceName { get; set; }
    public string? RepoId { get; set; }
    public string? RepoName { get; set; }
    [AllowRules(Validation = RuleAccess.Allowed)]
    public string Name { get; set; }
    [AllowRules(Validation = RuleAccess.Allowed)]
    public string Url { get; set; }

    public MonitorConfigurationTypes MonitorConfigurationType { get; set; }
    [AllowRules(Validation = RuleAccess.Allowed, Default = RuleAccess.Allowed)]
    public MonitorSourceTypes? MonitorSourceType { get; set; }

    [BsonElement("MonitorSourcetypes")]
    [BsonIgnoreIfNull]
    public MonitorSourceTypes? LegacyMonitorSourceType { get; set; }

    [BsonIgnore]
    public MonitorSourceTypes EffectiveMonitorSourceType =>
        MonitorSourceType ?? LegacyMonitorSourceType ?? MonitorSourceTypes.DeployedServices;

    [AllowRules(Validation = RuleAccess.Allowed, Default = RuleAccess.Allowed)]
    public MonitorTypes? MonitorType { get; set; }
    [AllowRules(Validation = RuleAccess.Allowed, Default = RuleAccess.Allowed)]
    public ProtocolTypes? ProtocolType { get; set; }
    [AllowRules(Validation = RuleAccess.Allowed, Default = RuleAccess.Allowed)]
    public HttpMethodTypes? HttpMethodType { get; set; }
    [AllowRules(Validation = RuleAccess.Allowed, Default = RuleAccess.Allowed)]
    public AuthorizationTypes? AuthorizationType { get; set; }

    [AllowRules(Validation = RuleAccess.Allowed)]
    public int IntervalInSeconds { get; set; } = 30;
    [AllowRules(Validation = RuleAccess.Allowed)]
    public int TimeoutInSeconds { get; set; } = 60;
    [AllowRules(Validation = RuleAccess.Allowed)]
    public int GracePeriodInSeconds { get; set; } = 30;

    public DateTime? LastIncidentAt { get; set; }
    public DateTime? LastCheckedAt { get; set; }

    public bool IsActive { get; set; } = true;
    public bool CurrentStatus { get; set; } = true;

    [AllowRules(Validation = RuleAccess.Allowed, Default = RuleAccess.Allowed)]
    public string? ExpectedContent { get; set; }
    // May carry credentials (an Authorization header, a token in the body): length or shape checks only.
    [AllowRules(Validation = RuleAccess.Warned)]
    public string? CustomHttpHeaders { get; set; }
    [AllowRules(Validation = RuleAccess.Warned)]
    public string? CustomPayload { get; set; }
    // Lists start empty, never null, so a default would never apply: validation only.
    [AllowRules(Validation = RuleAccess.Allowed)]
    public List<string> SuccessHttpResponseCodes { get; set; } = new List<string>();
    [AllowRules(Validation = RuleAccess.Allowed)]
    public List<string> Regions { get; set; } = new List<string>();
    [AllowRules(Validation = RuleAccess.Allowed)]
    public List<string> Emails { get; set; } = new List<string>();
    public List<IncidentListSummary> IncidentSummaries { get; set; } = new List<IncidentListSummary>();
}
