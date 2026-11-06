namespace Blocks.Genesis.Entities.Release;

/// <summary>Values of <see cref="SastReport.Status"/> and <see cref="ScaReport.Status"/>.</summary>
public static class AnalysisReportStatus
{
    /// <summary>The build has started and its results are not saved yet.</summary>
    public const string Pending = "Pending";
    /// <summary>The results are saved.</summary>
    public const string Ready = "Ready";
    /// <summary>The scan step did not succeed for this build, so there are no results to save.</summary>
    public const string NotRun = "NotRun";
    /// <summary>The retries ran out without the results being saved.</summary>
    public const string Failed = "Failed";
}
