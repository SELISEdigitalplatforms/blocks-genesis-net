using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace XUnitTest.Entities.Os;

/// <summary>Package classes moved from blocks-os (genesis SPEC1 6.4, folder Os/).</summary>
public class OsEntitiesTests
{
    // 3 x SHA-256 (first 16 bytes, hex): filled sample, default instance, all-null instance, of the BSON that the blocks-os SERVICE class wrote
    // (Common/EntitySamples: Filled, DefaultBson, NullBson), taken before the move from the feature/collection-cut working tree, 2026-10-13.
    // The package class must write exactly these bytes. A changed hash = a document change: owner decision, not a hash update.
    private static readonly Dictionary<string, string> ServiceClassBsonHashes = new(StringComparer.Ordinal)
    {
        ["Blocks.Genesis.Entities.Os.EmailTemplate"] = "4F5FC37B395375678FAB91975155D499 2352F950FED339064A050EC0A4D7511A 41699AF28DCC146FAE8754849D89FF4F",
        ["Blocks.Genesis.Entities.Os.IntegrationSetup"] = "0FE46A2C41CD05A253EB249562735C15 0C731DE406BD25D9A66858E7BCEB6EC9 BB96FE7FAC6BEF4BBF00A2A58067DB6E",
        ["Blocks.Genesis.Entities.Os.MailServerConfiguration"] = "05416EB5CC1D8CB64ABD98B9E8CE8536 4E64E05BA499E02F95363440D78CF196 C8D207DADE23B86DB7DD0F279726DD96",
        ["Blocks.Genesis.Entities.Os.MigrationTracker"] = "B754287A977710A0AF189F7CBF981D84 E3E9F6A6EACFA76A9B05982CFC743CAF 9E531C68F38C4AA4121FE61F41914A50",
        ["Blocks.Genesis.Entities.Os.NotificationConfiguration"] = "44DAA4D52F86A839259DD22190854EA0 6355951BD68EF9849EFEB17220062E3E E12738A6D2ECAE5397612DA0678E0760",
        ["Blocks.Genesis.Entities.Os.ResourceLimit"] = "7976D3709ED7AFC716C795ABB64CD082 05A874D57B670881E235ADF7879190E1 C7861B41C5629ABF18539F8E1395F768",
        ["Blocks.Genesis.Entities.Os.ServiceMigrationStatus"] = "734C5D156AC1BA21CF7CD46F41B6B290 9C10A420C9B4522BD3C3290213BCA70F 9C10A420C9B4522BD3C3290213BCA70F",
        ["Blocks.Genesis.Entities.Os.ThirdPartyJWTClaims"] = "558A69FCD3D94151317489A1866E2BC8 7AD1DC03E69173D527DD3FC25C892030 8977FAFC5089CE10B270DBDDFF528C68",
    };

    public static TheoryData<string> DocumentClasses() => EntityAssertions.DocumentClassesOf("Os");

    public static TheoryData<string> CatalogClasses() => EntityAssertions.CatalogClassesOf("os");

    [Fact]
    public void GoldenBytes_HashForEveryClass() => EntityAssertions.HashesCoverEveryClass("Os", ServiceClassBsonHashes);

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
    public void MailServerConfiguration_NullSnsFlag_ReadsAsFalse()
    {
        var doc = new Blocks.Genesis.Entities.Os.MailServerConfiguration { ItemId = "m1" }.ToBsonDocument();
        doc["IsEnableSnsConfiguration"] = BsonNull.Value; // written by today's logic copy (bool?)
        Assert.False(BsonSerializer.Deserialize<Blocks.Genesis.Entities.Os.MailServerConfiguration>(doc).IsEnableSnsConfiguration);
    }
}
