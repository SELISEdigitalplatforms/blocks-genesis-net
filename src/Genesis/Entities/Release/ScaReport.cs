using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Release;

/// <summary>The Dependency-Track results of one build, saved on <see cref="Build.ScaReport"/>.</summary>
[BsonIgnoreExtraElements]
public class ScaReport
{
    public string Status { get; set; } = AnalysisReportStatus.Pending;
    public int Attempts { get; set; }
    public DateTime? CapturedAt { get; set; }
    public string? Message { get; set; }

    // The Dependency-Track project (repository + branch) and BOM the results came from.
    public string? ProjectUuid { get; set; }
    public string? ProjectName { get; set; }
    public string? ProjectVersion { get; set; }
    public DateTime? BomImportedAt { get; set; }
    public DateTime? AnalyzedAt { get; set; }

    public ScaMetrics? Metrics { get; set; }
    public List<ScaFinding> Findings { get; set; } = new();
    // True when only part of the findings fit on the build document.
    public bool FindingsTruncated { get; set; }
}

/// <summary>The project metrics as Dependency-Track reports them.</summary>
[BsonIgnoreExtraElements]
public class ScaMetrics
{
    public int Critical { get; set; }
    public int High { get; set; }
    public int Medium { get; set; }
    public int Low { get; set; }
    public int Unassigned { get; set; }
    public int Vulnerabilities { get; set; }
    public int VulnerableComponents { get; set; }
    public int Components { get; set; }
    public int Suppressed { get; set; }
    public int FindingsTotal { get; set; }
    public int FindingsAudited { get; set; }
    public int FindingsUnaudited { get; set; }
    public double InheritedRiskScore { get; set; }
    public int Kev { get; set; }
    public int PolicyViolationsFail { get; set; }
    public int PolicyViolationsWarn { get; set; }
    public int PolicyViolationsInfo { get; set; }
    public int PolicyViolationsTotal { get; set; }
    public int PolicyViolationsAudited { get; set; }
    public int PolicyViolationsUnaudited { get; set; }
    public int PolicyViolationsSecurityTotal { get; set; }
    public int PolicyViolationsSecurityAudited { get; set; }
    public int PolicyViolationsSecurityUnaudited { get; set; }
    public int PolicyViolationsLicenseTotal { get; set; }
    public int PolicyViolationsLicenseAudited { get; set; }
    public int PolicyViolationsLicenseUnaudited { get; set; }
    public int PolicyViolationsOperationalTotal { get; set; }
    public int PolicyViolationsOperationalAudited { get; set; }
    public int PolicyViolationsOperationalUnaudited { get; set; }
    public string? CollectionLogic { get; set; }
    public bool CollectionLogicChanged { get; set; }
    public long? FirstOccurrence { get; set; }
    public long? LastOccurrence { get; set; }
}

/// <summary>One vulnerability in one component.</summary>
[BsonIgnoreExtraElements]
public class ScaFinding
{
    public string? ComponentName { get; set; }
    public string? ComponentGroup { get; set; }
    public string? ComponentVersion { get; set; }
    public string? LatestVersion { get; set; }
    public string? VulnId { get; set; }
    public string? Source { get; set; }
    public string? Severity { get; set; }
    public double? CvssV3BaseScore { get; set; }
    public double? CvssV4Score { get; set; }
    public double? EpssScore { get; set; }
    public double? EpssPercentile { get; set; }
    public string? CweName { get; set; }
    public List<ScaCwe> Cwes { get; set; } = new();
    public string? Description { get; set; }
    // Plain identifiers; links are built when the report is read.
    public List<ScaAlias> Aliases { get; set; } = new();
}

[BsonIgnoreExtraElements]
public class ScaCwe
{
    public int CweId { get; set; }
    public string? Name { get; set; }
}

[BsonIgnoreExtraElements]
public class ScaAlias
{
    public string? CveId { get; set; }
    public string? GhsaId { get; set; }
    public string? OsvId { get; set; }
}
