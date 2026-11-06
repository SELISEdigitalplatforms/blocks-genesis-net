using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Release;

/// <summary>The SonarQube results of one build, saved on <see cref="Build.SastReport"/>.</summary>
[BsonIgnoreExtraElements]
public class SastReport
{
    public string Status { get; set; } = AnalysisReportStatus.Pending;
    public int Attempts { get; set; }
    public DateTime? CapturedAt { get; set; }
    public string? Message { get; set; }

    // The SonarQube analysis the results came from.
    public string? ProjectKey { get; set; }
    public string? Branch { get; set; }
    public string? AnalysisKey { get; set; }
    public DateTime? AnalysisDate { get; set; }
    public string? Revision { get; set; }

    // Metric key -> value as SonarQube sent it, plus new_code_period_date and new_code_period_mode: the
    // "details" the SAST report returns. A dictionary, so a new metric key needs no package release.
    public Dictionary<string, string> Metrics { get; set; } = new();
    public SastQualityGate? QualityGate { get; set; }
    public SastIssueBreakdown? IssueBreakdown { get; set; }
}

[BsonIgnoreExtraElements]
public class SastQualityGate
{
    public string Status { get; set; } = "NONE"; // OK | ERROR | NONE
    public List<SastGateCondition> Conditions { get; set; } = new();
}

[BsonIgnoreExtraElements]
public class SastGateCondition
{
    public string MetricKey { get; set; } = "";
    public string Comparator { get; set; } = "GT"; // GT | LT
    public string ErrorThreshold { get; set; } = "";
    public string? ActualValue { get; set; }
    public string Status { get; set; } = "OK"; // OK | ERROR
}

/// <summary>Open issue counts by severity, for new code and for the whole branch.</summary>
[BsonIgnoreExtraElements]
public class SastIssueBreakdown
{
    public SastSeverityCounts? NewCode { get; set; }
    public SastSeverityCounts? Overall { get; set; }
}

[BsonIgnoreExtraElements]
public class SastSeverityCounts
{
    public int Total { get; set; }
    public int Blocker { get; set; }
    public int High { get; set; }
    public int Medium { get; set; }
    public int Low { get; set; }
    public int Info { get; set; }
}
