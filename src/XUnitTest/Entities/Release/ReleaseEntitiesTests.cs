using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace XUnitTest.Entities.Release;

/// <summary>Package classes moved from blocks-release (genesis SPEC1 6.4, folder Release/).</summary>
public class ReleaseEntitiesTests
{
    // 3 x SHA-256 (first 16 bytes, hex): filled sample, default instance, all-null instance, of the BSON that the blocks-release SERVICE class wrote
    // (Common/EntitySamples: Filled, DefaultBson, NullBson), taken before the move from the feature/collection-cut working tree, 2026-10-13.
    // The package class must write exactly these bytes. A changed hash = a document change: owner decision, not a hash update.
    private static readonly Dictionary<string, string> ServiceClassBsonHashes = new(StringComparer.Ordinal)
    {
        // Filled-sample hashes of every BaseEntity class changed on purpose 2026-10-14 (owner decision): BaseEntity gained
        // nullable Create/Read/Update/Delete access lists ([BsonIgnoreIfNull]); default and all-null bytes are unchanged.
        // Build and Repo changed on purpose 2026-10-29 (owner decision): both gained IsCustomDomainOnly (false = deploy to
        // both domains, as before); Build gained SastReport and ScaReport ([BsonIgnoreIfNull], so absent until saved).
        ["Blocks.Genesis.Entities.Release.Build"] = "DAD34BF77D78AD4694165B778855023E A0FDA32448218AECF843DC43597D572A 88BE8C9DC2D9B653020CE05DB7E10331",
        ["Blocks.Genesis.Entities.Release.BuildEventResponse"] = "3367132160B81DF9D5515A885B5B90E2 3C414B1A90A43D4BB9EF114DA7058D7B 3C414B1A90A43D4BB9EF114DA7058D7B",
        ["Blocks.Genesis.Entities.Release.Config"] = "6FA9849E1F9853C6E27B21EA9D3F748E 4C39D9ED43A5A7AFB0EB8DB06A32C897 4C39D9ED43A5A7AFB0EB8DB06A32C897",
        ["Blocks.Genesis.Entities.Release.DependencyTrackProjects"] = "278845AC5CA3540D128D868744548143 ABA5753770818794440ACEDDCD6E340F 686B0E4666C8BDEF5B261DDA164989F3",
        ["Blocks.Genesis.Entities.Release.DeploySettings"] = "8A6EB5EA64E7092CB69D736997115CAB 5DF5711AD5A89A677888B49ACE736C87 5DF5711AD5A89A677888B49ACE736C87",
        ["Blocks.Genesis.Entities.Release.GithubWebhook"] = "455A1D78392A6656EA759657BC7B2248 CB8A28735CF68C794D8757CD46C49AD2 CB8A28735CF68C794D8757CD46C49AD2",
        ["Blocks.Genesis.Entities.Release.HostingProvider"] = "5E17253C00E43FE253E6FEA5CF797836 7C56A258BEA7521010CCADBC4D6DCCAC 7C56A258BEA7521010CCADBC4D6DCCAC",
        ["Blocks.Genesis.Entities.Release.MachineConfig"] = "8996975AB0B993293721DAC10C0A4C07 6C277305550CD5937D5A6D94A445C92F 6C277305550CD5937D5A6D94A445C92F",
        ["Blocks.Genesis.Entities.Release.Region"] = "403FD4B64A2AE8FA9BF74850C4EA957A F27865FBCBECDC3E2AD168DE03903785 F27865FBCBECDC3E2AD168DE03903785",
        ["Blocks.Genesis.Entities.Release.Repo"] = "502E9DF288F1054FD1CDD7ADA01A6915 C7E52AB597492B983FFDBEF6DFBCE3A2 4E628991D5406CF56821F4E41E4C79EF",
        ["Blocks.Genesis.Entities.Release.RepoProject"] = "94859BA0BDB5F98802F166066C741855 B143688ED381AD2C6AB76185B7638CA1 B143688ED381AD2C6AB76185B7638CA1",
        ["Blocks.Genesis.Entities.Release.RepositoryWebhook"] = "78FCB872824A70685534729C4B680858 1BD71B4551BCFB008C11A33B2211A23F BD006A8ACC08BBC4002A598FE00CE275",
        // New package classes 2026-10-29, no service class before them: the per-build analysis results on Build.
        ["Blocks.Genesis.Entities.Release.SastGateCondition"] = "2643003BF244A0C9C912C02A7A090E1B 9B51BBD463D65382B32B41DD3EB7974E 3FAB084B599910B3024A3CD4402D1E4C",
        ["Blocks.Genesis.Entities.Release.SastIssueBreakdown"] = "9986F9751D740F3515E402626B58197C 75C5F5441E7B0AA9472B9801D2428935 75C5F5441E7B0AA9472B9801D2428935",
        ["Blocks.Genesis.Entities.Release.SastQualityGate"] = "03DD81792EE71DECC4929C2671CBE949 1A8038D5A07C3B953FDD0F9C851974E6 FBC9C8245F3B385DBF0E03C457391740",
        ["Blocks.Genesis.Entities.Release.SastReport"] = "24DE7B8FC1F6EA1332AFAD580C7D1D7E 0102BF0B666631EF48789D0DB87E0D83 E4B67F1D0AF65C7C83A15C36EE5D7F5A",
        ["Blocks.Genesis.Entities.Release.SastSeverityCounts"] = "6A55A2F80D0421ACC5D7A7BD5CC1C5A2 DED32DE9A0876C88D884ACEEE1EE4D9C DED32DE9A0876C88D884ACEEE1EE4D9C",
        ["Blocks.Genesis.Entities.Release.ScaAlias"] = "6676363321DDB9869D9684F88949970F 42AF0C86E93DB61AE201880555DDBF06 42AF0C86E93DB61AE201880555DDBF06",
        ["Blocks.Genesis.Entities.Release.ScaCwe"] = "FD264A4E6473D85A9802B5FAFA96DAF4 BFF767DD66EFBCD17D946CE6CE5BCDA0 BFF767DD66EFBCD17D946CE6CE5BCDA0",
        ["Blocks.Genesis.Entities.Release.ScaFinding"] = "1AE59B496A2047BBB1EFB5AD4C5F118B C79B1C26512F1EB86E522E8C5AEFE9B1 8CFE6A603B83C3B738A1963C64723570",
        ["Blocks.Genesis.Entities.Release.ScaMetrics"] = "39BC21808D237FB6A28F166FE23ED826 ACD3BC9390E49EA5E0316D1CB5EC69D2 ACD3BC9390E49EA5E0316D1CB5EC69D2",
        ["Blocks.Genesis.Entities.Release.ScaReport"] = "8BA24B8451285FF419DC95B818512563 2AF02C33E826CDE17072143A67EB7D9E 52F4735B1681F99060FBC2E2D7872119",
    };

    public static TheoryData<string> DocumentClasses() => EntityAssertions.DocumentClassesOf("Release");

    public static TheoryData<string> CatalogClasses() => EntityAssertions.CatalogClassesOf("release");

    [Fact]
    public void GoldenBytes_HashForEveryClass() => EntityAssertions.HashesCoverEveryClass("Release", ServiceClassBsonHashes);

    [Theory]
    [MemberData(nameof(DocumentClasses))]
    public void GoldenBytes_SameAsServiceClassBeforeTheMove(string fullName) => EntityAssertions.GoldenBytes(fullName, ServiceClassBsonHashes);

    [Theory]
    [MemberData(nameof(DocumentClasses))]
    public void ClassMap_Builds(string fullName) => EntityAssertions.ClassMapBuilds(fullName);

    [Theory]
    [MemberData(nameof(CatalogClasses))]
    public void CatalogEntity_NameBaseAndIgnoreExtraElements(string cls) => EntityAssertions.CatalogEntity(cls);

    [Fact]
    public void Repo_WithoutIsCustomDomainOnly_ReadsAsDeployToBothDomains()
    {
        var stored = new BsonDocument { { "_id", "repo-1" }, { "CustomDeploymentUrl", "app.example.com" } };

        var read = BsonSerializer.Deserialize<Blocks.Genesis.Entities.Release.Repo>(stored);

        Assert.False(read.IsCustomDomainOnly);
        Assert.Equal("app.example.com", read.CustomDeploymentUrl);
    }

    [Fact]
    public void Build_WithoutReports_WritesNoReportFields_AndOlderBuildsReadWithNone()
    {
        var written = new Blocks.Genesis.Entities.Release.Build { ItemId = "build-1" }.ToBsonDocument();
        Assert.False(written.Contains("SastReport"));
        Assert.False(written.Contains("ScaReport"));

        var read = BsonSerializer.Deserialize<Blocks.Genesis.Entities.Release.Build>(new BsonDocument { { "_id", "build-1" }, { "Branch", "dev" } });
        Assert.Null(read.SastReport);
        Assert.Null(read.ScaReport);
        Assert.False(read.IsCustomDomainOnly);
    }

    [Fact]
    public void Reports_StartPending_WithEmptyResults()
    {
        var sast = new Blocks.Genesis.Entities.Release.SastReport();
        var sca = new Blocks.Genesis.Entities.Release.ScaReport();

        Assert.Equal(Blocks.Genesis.Entities.Release.AnalysisReportStatus.Pending, sast.Status);
        Assert.Empty(sast.Metrics);
        Assert.Equal(Blocks.Genesis.Entities.Release.AnalysisReportStatus.Pending, sca.Status);
        Assert.Empty(sca.Findings);
        Assert.False(sca.FindingsTruncated);
    }

    [Fact]
    public void Build_WithReports_RoundTripsEveryValue()
    {
        var build = new Blocks.Genesis.Entities.Release.Build
        {
            ItemId = "build-1",
            IsCustomDomainOnly = true,
            SastReport = new Blocks.Genesis.Entities.Release.SastReport
            {
                Status = Blocks.Genesis.Entities.Release.AnalysisReportStatus.Ready,
                Revision = "13373126bec50572acc306322d39b8cd927b0993",
                Metrics = new() { ["new_coverage"] = "0.0", ["quality_gate_details"] = "{\"level\":\"ERROR\"}", ["new_code_period_date"] = "2026-08-10T09:24:13+0000" },
                QualityGate = new() { Status = "ERROR", Conditions = [new() { MetricKey = "new_violations", Comparator = "GT", ErrorThreshold = "0", ActualValue = "367", Status = "ERROR" }] },
                IssueBreakdown = new() { NewCode = new() { Total = 367, High = 9, Medium = 174, Low = 184 } },
            },
            ScaReport = new Blocks.Genesis.Entities.Release.ScaReport
            {
                Status = Blocks.Genesis.Entities.Release.AnalysisReportStatus.Ready,
                ProjectVersion = "dev",
                Metrics = new() { High = 3, Medium = 4, Low = 1, Components = 256, InheritedRiskScore = 28.0, FirstOccurrence = 1793146274587 },
                Findings =
                [
                    new()
                    {
                        ComponentName = "vite", ComponentVersion = "7.3.6", LatestVersion = "8.3.5", VulnId = "GHSA-587h-7wmj-h5pm",
                        Source = "GITHUB", Severity = "HIGH", CvssV4Score = 7.5, EpssScore = 0.0004, EpssPercentile = 0.12,
                        Cwes = [new() { CweId = 77, Name = "Command Injection" }], Description = new string('d', 6000),
                        Aliases = [new() { CveId = "CVE-2026-112942" }],
                    },
                ],
            },
        };

        var read = BsonSerializer.Deserialize<Blocks.Genesis.Entities.Release.Build>(build.ToBsonDocument());

        Assert.True(read.IsCustomDomainOnly);
        Assert.Equal(build.SastReport.Metrics, read.SastReport!.Metrics);
        Assert.Equal("13373126bec50572acc306322d39b8cd927b0993", read.SastReport.Revision);
        Assert.Equal("367", read.SastReport.QualityGate!.Conditions.Single().ActualValue);
        Assert.Equal(174, read.SastReport.IssueBreakdown!.NewCode!.Medium);
        Assert.Null(read.SastReport.IssueBreakdown.Overall);

        var finding = read.ScaReport!.Findings.Single();
        Assert.Equal(28.0, read.ScaReport.Metrics!.InheritedRiskScore);
        Assert.Equal(1793146274587, read.ScaReport.Metrics.FirstOccurrence);
        Assert.Null(finding.CvssV3BaseScore);
        Assert.Equal(7.5, finding.CvssV4Score);
        Assert.Equal(6000, finding.Description!.Length);
        Assert.Equal(77, finding.Cwes.Single().CweId);
        Assert.Equal("CVE-2026-112942", finding.Aliases.Single().CveId);
    }
}
