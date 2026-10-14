using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace XUnitTest.Entities.Data;

/// <summary>Package classes moved from blocks-data (genesis SPEC1 6.4, folder Data/).</summary>
public class DataEntitiesTests
{
    // 3 x SHA-256 (first 16 bytes, hex): filled sample, default instance, all-null instance, of the BSON that the blocks-data SERVICE class wrote
    // (Common/EntitySamples: Filled, DefaultBson, NullBson), taken before the move from the feature/collection-cut working tree, 2026-10-13.
    // The package class must write exactly these bytes. A changed hash = a document change: owner decision, not a hash update.
    private static readonly Dictionary<string, string> ServiceClassBsonHashes = new(StringComparer.Ordinal)
    {
["Blocks.Genesis.Entities.Data.DataAccessPolicy"] = "CD27FF041C5DEC7CCC8886FBD5903A62 6A695E651DB981256B0E177E35852C82 0E73D66ADCC5906B571E56644D99B420",
        ["Blocks.Genesis.Entities.Data.DataValidation"] = "AC212F1CCC55103625507D787617B53D DFB7A9CCDA9E6088D308428A545D1816 A794FC235E46D909A021A27310985753",
        ["Blocks.Genesis.Entities.Data.DirectoryOptions"] = "EE16F317F1AA907A659C7A547CA30606 6C6B04A509549608DF6ED1571C09AAE6 6C6B04A509549608DF6ED1571C09AAE6",
        ["Blocks.Genesis.Entities.Data.FieldDefinition"] = "B001629F61DE1B6D99D7600ED920FE11 FECF3B7A163A5E3A666156C6EE357EBB B508DDF439696D5918EACF18DF04B6B7",
        ["Blocks.Genesis.Entities.Data.File"] = "5DF60133B809B6E4338CBAFC7086ACC0 E19C2D8BB8A91C9F7D370E1961A320DC F344A0A366D6ADD44EDBD8EAF580506A",
        ["Blocks.Genesis.Entities.Data.FileDirectory"] = "C37D3E82EF67E4AE44E8530FC005FB74 83D93A423CE0FE53DF175014448DF641 777FA3862ED89FED7308D6E4E1D0978F",
        ["Blocks.Genesis.Entities.Data.FileVersion"] = "A27631028CC7B1CD9EE8AAEC62E29751 23FA024E6E255009E9499D0E8544104A 26FE054AF1178A5075FDF396119E1966",
        ["Blocks.Genesis.Entities.Data.FileVersionOptions"] = "2277CB4AB04DF2C0335772B94DED8C2E 30671AEB2D45B9AB88E250D3D7ECAD62 91135FEE55620FC7585833AF395A8DB3",
        ["Blocks.Genesis.Entities.Data.GraphQlBaseEntity"] = "F7CDD73E3A3E5F53EC7C47736123A7E8 F793F434B8BEDDC486F79C1CEB6A79B3 60698ABE2D8500B39255F20998486E2F",
        ["Blocks.Genesis.Entities.Data.IndexFieldSpec"] = "EA8FD96FDF7074373048945DCA34CB81 A0E33D4EC9F5D4D42B6B0F5CADB3336F 62F63C7986E2A303022382B94C1644A9",
        ["Blocks.Genesis.Entities.Data.MetaValue"] = "ED1436792CFE3CF26538167C13A9D26B C167ADB1195F86BE81BABA9B0A8EC42F C167ADB1195F86BE81BABA9B0A8EC42F",
        ["Blocks.Genesis.Entities.Data.ObjectAccessPolicy"] = "BC02BAD38CF4AAD55B1EA81A6B0ABBAE B6C0FD683ABE5190BD10485C0B28B2C8 9AE0610395DD6110813A894BC0072F2E",
        ["Blocks.Genesis.Entities.Data.ObjectAuditLog"] = "13244B533ECC4F918568DE6B17D059BF B584BFF951B8CEC0AFE6278B2C042E2A 5167A16028F81046238EACCAA1C782D0",
        ["Blocks.Genesis.Entities.Data.ObjectItem"] = "76AC675AA064829B0720DA360DE4A0F9 97A368858EBF96B0571CC1B8AD99F856 0892EA6DE51D962A4BEFF42E27EC9C14",
        ["Blocks.Genesis.Entities.Data.PolicyRule"] = "6B0EE4EF7241162B918FF4F045D87CFC FDC85D3C01476258C1106DE4E9EAFB31 A1AE50A2369F289804FAF5A98E56E2A8",
        ["Blocks.Genesis.Entities.Data.PolicyRuleGroup"] = "F13DC2A1D448F355081810979573C62B 15ACC7716EBE5339F64AD28F47F94D71 9438B75CED527DDFE366FB3FD54EC61C",
        ["Blocks.Genesis.Entities.Data.PublishedSchemaSnapshot"] = "3E8565087B4B53D07231FC15787B01D6 CCBB37BB38EB1B4CDF6C437336353B17 FFD6962D73D8AB769EE7DF012A8B6CE5",
        ["Blocks.Genesis.Entities.Data.PublishedSchemaSnapshotChunk"] = "F0BC5558E5DFF54FB5F3AB1AD672F826 39A3224787AC4BD58BA6A7E839DB7D21 EABE05137E8B1FBA5F316AB437AAE9C4",
        ["Blocks.Genesis.Entities.Data.SchemaChangeLog"] = "0FB90FABF29A396401B0F7271EF5CEFE F850AB9CE17E6ACE013DBDADD023AF4D EDBC76BFFF2286CFFB7B1A856BF73627",
        // Filled-sample hash changed on purpose (owner decision 2026-10-14): IsSystemEntity added with [BsonIgnoreIfDefault];
        // default and all-null bytes are unchanged, so existing documents are byte-identical.
        ["Blocks.Genesis.Entities.Data.SchemaDefinition"] = "918A428CD45E5C513F08870A5393D2F5 726AC8DACD7D8DBF541656949A6F860C 2FC7AD4B65B84C2E950156758FCEBB42",
        ["Blocks.Genesis.Entities.Data.SchemaIndexDefinition"] = "AD85DAF0EBA51A06F1BD95D36DD8D644 FB9DDD0578BE6B3A9BD24659069AA32D 616BBA5DB2DBB248F1CDF28D9EAA834C",
        ["Blocks.Genesis.Entities.Data.SchemaPublishState"] = "2197FA54CEF058AEFC3090A26847EE99 E43A501BC314F74CA65A562EA88DB865 ACAE91E1BFF76FE025F95D75A46CBE23",
        ["Blocks.Genesis.Entities.Data.StorageConfiguration"] = "34C9233FB47FC39E532EEA385FBF7EA5 9DAD29AED145D1822D2B307158CAB7BB 061A848E4BB93A76E8768449890E9AAF",
        ["Blocks.Genesis.Entities.Data.Structure"] = "C2265CDBB36D4ACEA8CEF9707AD97F93 EDDD120AC2690E72E02133125F589458 1501A18A1B14C70A101426BDCF826ECC",
        ["Blocks.Genesis.Entities.Data.ValidationRule"] = "1F9381ABF4AE57013608C73A820D28C2 B0875FBB77CB7FFEFD00B2A6F9903403 B0875FBB77CB7FFEFD00B2A6F9903403",
    };

    public static TheoryData<string> DocumentClasses() => EntityAssertions.DocumentClassesOf("Data");

    public static TheoryData<string> CatalogClasses() => EntityAssertions.CatalogClassesOf("data");

    [Fact]
    public void GoldenBytes_HashForEveryClass() => EntityAssertions.HashesCoverEveryClass("Data", ServiceClassBsonHashes);

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
    public void StorageConfiguration_UploadCompletionNames_StoredAsStrings_UnknownNameThrows()
    {
        var doc = new Blocks.Genesis.Entities.Data.StorageConfiguration { ItemId = "s1" }.ToBsonDocument();
        doc["UploadCompletionRequiredFor"] = new BsonArray { "Public", "Private" }; // os/logic copies write List<string>
        var read = BsonSerializer.Deserialize<Blocks.Genesis.Entities.Data.StorageConfiguration>(doc);
        Assert.Equal(new BsonArray { "Public", "Private" }, read.ToBsonDocument()["UploadCompletionRequiredFor"]);

        // The risk behind the read-only pre-check before the os/logic switch (C9).
        doc["UploadCompletionRequiredFor"] = new BsonArray { "NotAModifier" };
        Assert.ThrowsAny<FormatException>(() => BsonSerializer.Deserialize<Blocks.Genesis.Entities.Data.StorageConfiguration>(doc));
    }
}
