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
        // Filled-sample hashes of every BaseEntity class changed on purpose 2026-10-14 (owner decision): BaseEntity gained
        // nullable Create/Read/Update/Delete access lists ([BsonIgnoreIfNull]); default and all-null bytes are unchanged.
        ["Blocks.Genesis.Entities.Os.EmailTemplate"] = "4615E5D5CC01F2BE1F0C84D58579C442 2352F950FED339064A050EC0A4D7511A 41699AF28DCC146FAE8754849D89FF4F",
        // IntegrationRequest and IntegrationTemplate: hashes of the blocks-os service classes as moved from the dev working tree, 2026-10-15.
        ["Blocks.Genesis.Entities.Os.IntegrationRequest"] = "7586EC925992892D6186B4DF7C500EB6 3C668F1E8C610723F9F2D9D01F94D61B CDFEC7DF2D6F0EDC2E0D540A4FC8E8FC",
        ["Blocks.Genesis.Entities.Os.IntegrationSetup"] = "0F15C211144A321B9C6CB1AF055AF253 0C731DE406BD25D9A66858E7BCEB6EC9 BB96FE7FAC6BEF4BBF00A2A58067DB6E",
        ["Blocks.Genesis.Entities.Os.IntegrationTemplate"] = "89990FABAA57B5620D2A2D79A7DE6F54 157810C3CD79264E104FA0BB1F8D62BD F3FEE82C32F4B5DD9E4436B56C0DE8AA",
        // Filled-sample hash changed on purpose 2026-10-27 (owner decision): Blocks Secrets reference AccountPasswordReference,
        // [BsonIgnoreIfNull], so the default and all-null bytes are unchanged.
        ["Blocks.Genesis.Entities.Os.MailServerConfiguration"] = "34255D76FF10A6650E9209DF630E3BCB 4E64E05BA499E02F95363440D78CF196 C8D207DADE23B86DB7DD0F279726DD96",
        ["Blocks.Genesis.Entities.Os.MigrationTracker"] = "6815B920AE0725C6F18708A3696C81B5 E3E9F6A6EACFA76A9B05982CFC743CAF 9E531C68F38C4AA4121FE61F41914A50",
        // Filled-sample hash changed on purpose 2026-10-21 (owner decision): optional built-in fields (IsBuiltIn, IsDisabled,
        // Description, Importance, Kind, SourceService, ConsumerAppRecommendation, ConsumerAppNote), all left out while default,
        // so the default and all-null bytes are unchanged.
        ["Blocks.Genesis.Entities.Os.NotificationConfiguration"] = "FE8FEAE69C79B18B091B1CF56E7206A3 6355951BD68EF9849EFEB17220062E3E E12738A6D2ECAE5397612DA0678E0760",
        ["Blocks.Genesis.Entities.Os.ResourceLimit"] = "DEB5AECD71D8731083FCF03B9617E607 05A874D57B670881E235ADF7879190E1 C7861B41C5629ABF18539F8E1395F768",
        ["Blocks.Genesis.Entities.Os.ServiceMigrationStatus"] = "734C5D156AC1BA21CF7CD46F41B6B290 9C10A420C9B4522BD3C3290213BCA70F 9C10A420C9B4522BD3C3290213BCA70F",
        ["Blocks.Genesis.Entities.Os.ThirdPartyJWTClaims"] = "974BE3DA9AD4C1FB5FD9835A16EFDD65 7AD1DC03E69173D527DD3FC25C892030 8977FAFC5089CE10B270DBDDFF528C68",
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

    [Fact]
    public void MailServerConfiguration_AccountPasswordReference_NotStoredWhenNull_OldDocumentKeepsPlaintext()
    {
        var doc = new Blocks.Genesis.Entities.Os.MailServerConfiguration { ItemId = "m1", AccountPassword = "plain" }.ToBsonDocument();
        Assert.False(doc.Contains("AccountPasswordReference"));

        // A document saved before the reference existed: plaintext read as before, no reference.
        var read = BsonSerializer.Deserialize<Blocks.Genesis.Entities.Os.MailServerConfiguration>(doc);
        Assert.Equal("plain", read.AccountPassword);
        Assert.Null(read.AccountPasswordReference);

        doc["AccountPassword"] = string.Empty; // blocks-os writing through Blocks Secrets
        doc["AccountPasswordReference"] = "s1";
        Assert.Equal("s1", BsonSerializer.Deserialize<Blocks.Genesis.Entities.Os.MailServerConfiguration>(doc).AccountPasswordReference);
    }
}
