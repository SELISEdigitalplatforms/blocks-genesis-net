namespace XUnitTest.Entities.Monitor;

/// <summary>Package classes moved from blocks-monitor (folder Monitor/).</summary>
public class MonitorEntitiesTests
{
    // 3 x SHA-256 (first 16 bytes, hex): filled sample, default instance, all-null instance, of the BSON that the blocks-monitor SERVICE class wrote
    // (Common/EntitySamples: Filled, DefaultBson, NullBson), taken from blocks-monitor dev (ecf336e) before the move, 2026-10-19.
    // The package class must write exactly these bytes. A changed hash = a document change: owner decision, not a hash update.
    private static readonly Dictionary<string, string> ServiceClassBsonHashes = new(StringComparer.Ordinal)
    {
        ["Blocks.Genesis.Entities.Monitor.IncidentListSummary"] = "FA8436CFB89FACAEB0554C658BFB144E D4F8E34AE2173F47C7B000B3EEDD4DD8 D4F8E34AE2173F47C7B000B3EEDD4DD8",
        ["Blocks.Genesis.Entities.Monitor.MonitorConfiguration"] = "A44862F2CBC05B1576F9F5752170337F F5DDEC893949F3DD001B52772DC6D884 8B86DC13200D5B61917A3E49269670BA",
        ["Blocks.Genesis.Entities.Monitor.MonitorIncident"] = "B26D0E71F239B54F3BBF16AC4C9E64DB ACD404C9C3130828F503DBCAAD2E0DB1 57C5A47AAF5060D8ECC06A4FA1278221",
        ["Blocks.Genesis.Entities.Monitor.MonitorPingLog"] = "0F77D75E7E67D752DAC7B25C5EA32C1D 947972610BC1ADDC7380661F78FC5219 947972610BC1ADDC7380661F78FC5219",
    };

    public static TheoryData<string> DocumentClasses() => EntityAssertions.DocumentClassesOf("Monitor");

    public static TheoryData<string> CatalogClasses() => EntityAssertions.CatalogClassesOf("monitor");

    [Fact]
    public void GoldenBytes_HashForEveryClass() => EntityAssertions.HashesCoverEveryClass("Monitor", ServiceClassBsonHashes);

    [Theory]
    [MemberData(nameof(DocumentClasses))]
    public void GoldenBytes_SameAsServiceClassBeforeTheMove(string fullName) => EntityAssertions.GoldenBytes(fullName, ServiceClassBsonHashes);

    [Theory]
    [MemberData(nameof(DocumentClasses))]
    public void ClassMap_Builds(string fullName) => EntityAssertions.ClassMapBuilds(fullName);

    [Theory]
    [MemberData(nameof(CatalogClasses))]
    public void CatalogEntity_NameBaseAndIgnoreExtraElements(string cls) => EntityAssertions.CatalogEntity(cls);
}
