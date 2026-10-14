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
        ["Blocks.Genesis.Entities.Release.Build"] = "028D4E90376C985691B08FC9E6B608C2 0BEE924FA8BDED51237C3F9F08D1EE54 657523507115BF3ADCF0CEC43F14DEE8",
        ["Blocks.Genesis.Entities.Release.BuildEventResponse"] = "3367132160B81DF9D5515A885B5B90E2 3C414B1A90A43D4BB9EF114DA7058D7B 3C414B1A90A43D4BB9EF114DA7058D7B",
        ["Blocks.Genesis.Entities.Release.Config"] = "6FA9849E1F9853C6E27B21EA9D3F748E 4C39D9ED43A5A7AFB0EB8DB06A32C897 4C39D9ED43A5A7AFB0EB8DB06A32C897",
        ["Blocks.Genesis.Entities.Release.DependencyTrackProjects"] = "A586800071804A06D191A0A5E17CE068 ABA5753770818794440ACEDDCD6E340F 686B0E4666C8BDEF5B261DDA164989F3",
        ["Blocks.Genesis.Entities.Release.DeploySettings"] = "8A6EB5EA64E7092CB69D736997115CAB 5DF5711AD5A89A677888B49ACE736C87 5DF5711AD5A89A677888B49ACE736C87",
        ["Blocks.Genesis.Entities.Release.GithubWebhook"] = "455A1D78392A6656EA759657BC7B2248 CB8A28735CF68C794D8757CD46C49AD2 CB8A28735CF68C794D8757CD46C49AD2",
        ["Blocks.Genesis.Entities.Release.HostingProvider"] = "5E17253C00E43FE253E6FEA5CF797836 7C56A258BEA7521010CCADBC4D6DCCAC 7C56A258BEA7521010CCADBC4D6DCCAC",
        ["Blocks.Genesis.Entities.Release.MachineConfig"] = "8996975AB0B993293721DAC10C0A4C07 6C277305550CD5937D5A6D94A445C92F 6C277305550CD5937D5A6D94A445C92F",
        ["Blocks.Genesis.Entities.Release.Region"] = "403FD4B64A2AE8FA9BF74850C4EA957A F27865FBCBECDC3E2AD168DE03903785 F27865FBCBECDC3E2AD168DE03903785",
        ["Blocks.Genesis.Entities.Release.Repo"] = "7E0E1E282225A071FF556929AABE8CEE 17B8901441BEC65E86BC1D1767528CE9 87E661A2A42BCE687CA95465030C78BE",
        ["Blocks.Genesis.Entities.Release.RepoProject"] = "94859BA0BDB5F98802F166066C741855 B143688ED381AD2C6AB76185B7638CA1 B143688ED381AD2C6AB76185B7638CA1",
        ["Blocks.Genesis.Entities.Release.RepositoryWebhook"] = "E3549579A3D5F2819C033F2C5FE44E36 1BD71B4551BCFB008C11A33B2211A23F BD006A8ACC08BBC4002A598FE00CE275",
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
}
