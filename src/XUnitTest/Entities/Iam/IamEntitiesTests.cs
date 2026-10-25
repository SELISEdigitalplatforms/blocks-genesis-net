using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace XUnitTest.Entities.Iam;

/// <summary>Package classes moved from blocks-iam (genesis SPEC1 6.4, folder Iam/).</summary>
public class IamEntitiesTests
{
    // 3 x SHA-256 (first 16 bytes, hex): filled sample, default instance, all-null instance, of the BSON that the blocks-iam SERVICE class wrote
    // (Common/EntitySamples: Filled, DefaultBson, NullBson), taken before the move from the feature/collection-cut working tree, 2026-10-13.
    // The package class must write exactly these bytes. A changed hash = a document change: owner decision, not a hash update.
    private static readonly Dictionary<string, string> ServiceClassBsonHashes = new(StringComparer.Ordinal)
    {
        // Filled-sample hashes of every BaseEntity class changed on purpose 2026-10-14 (owner decision): BaseEntity gained
        // nullable Create/Read/Update/Delete access lists ([BsonIgnoreIfNull]); default and all-null bytes are unchanged.
        ["Blocks.Genesis.Entities.Iam.ActivityContext"] = "A97F258A4399AF86954F0799EE430081 9B205DB2343CCC67F426A346CFE6BE46 9B205DB2343CCC67F426A346CFE6BE46",
        ["Blocks.Genesis.Entities.Iam.Address"] = "FBB4A8A81622A279E9E4A2EA67C01CC8 D8124768466AC60B756DE67E0677D0AF E94F48B5B77E65963C2CEE1C0A184C98",
        ["Blocks.Genesis.Entities.Iam.BiometricCredential"] = "C15F1347BFEA6DFE6ADE107268A86DC9 2C8CBBFA31B62516B47595C73B633598 77CB6946DC4376914FFB0A48264F24C1",
        ["Blocks.Genesis.Entities.Iam.BlocksClientConfig"] = "21EF47EFD285537CC43C4AF59C5D56A0 66725764A88CF05813E53E6709570169 C9F41DCA744732C666F7F28BF0FD33CF",
        ["Blocks.Genesis.Entities.Iam.BuiltInPermission"] = "22029E4D7174E9A2289C2C4AB6A71AE5 43B50CC8D787B0E83E5CF61C44378AF5 AAA3F3ED557686894496C947C166C86C",
        ["Blocks.Genesis.Entities.Iam.ClientCredential"] = "0FB43C75C4CB54961209757C9289056E 365D6CF7783C6978C3512063D072073B A1539E53AAD1FF5A09B9CD26D8F054E2",
        ["Blocks.Genesis.Entities.Iam.DeviceAuthorizationRequestModel"] = "01D92349D9D2333EB56B9E04286EAA13 1633FCD68935D98639D882BA444A2288 BF3B90ED5097B180932AEDDE27693D4E",
        ["Blocks.Genesis.Entities.Iam.DeviceInformation"] = "1808150F01E61EE5A85EC008F88C8060 77F22838EFC48F88F9CE54B1BF875C34 77F22838EFC48F88F9CE54B1BF875C34",
        ["Blocks.Genesis.Entities.Iam.ExternalIdentity"] = "5365F322978B7D6878C60DCFE9900B17 5D9B863D18EC094B6377F5C44051EF18 70E01D67BA79E0F703E5A68BF283CD4D",
        ["Blocks.Genesis.Entities.Iam.IdentityConfiguration"] = "F4550AF3895645DDE0CBB15D3148011B 78972763BEC26681804AC2AEBABB693B 003BED26623A96FC4323FB69C120B7C1",
        ["Blocks.Genesis.Entities.Iam.IdentityProvider"] = "A469A96D61839922CE735A47A42FCC55 E0A7768A540F7121B1103C62A5737C55 A5F2A7FD8B998DF9314D192556BCE80C",
        ["Blocks.Genesis.Entities.Iam.IdpSessionAccount"] = "04CE4A113EF054B300BCD25FA7D80F4B 17A2F635383973C477BF987B34536A31 C33F2A5EF1F3CC89AAAF43870B1CA295",
        ["Blocks.Genesis.Entities.Iam.IdpSessionModel"] = "71B88C318A5AE5EFDE15AEC73130E26F 00EEF75CE2CAEEDB7A477C2037699A79 6AFBCCD9D7C532BC65AC900D1530015A",
        ["Blocks.Genesis.Entities.Iam.ImpersonationSession"] = "1A2195E0C5C28045C82C069215940A2F 7B848E419145FA9BBFAEDCFC93D873BD AF0D9CB00EDAF0F64EAA19D33E1CC809",
        ["Blocks.Genesis.Entities.Iam.MfaConfiguration"] = "6BFE2F90667A9996A88720855C416B5B 31B4283C0E9FF88188F3E596638F2E53 4A392AF19DFF8F2D18EFA5AEB72ECD6D",
        ["Blocks.Genesis.Entities.Iam.MfaTemplate"] = "37C0B5BBCA615BB27A5D51953EABA266 584362245DA1DA0D63148E242712802C 584362245DA1DA0D63148E242712802C",
        ["Blocks.Genesis.Entities.Iam.OidcClientRegistration"] = "1E4D5BADCA66C34FD313DA7FBBA313D5 8C84A944934695E349CB07EC86392C3B F6432652E911FEFC91590CA0102CC2E9",
        ["Blocks.Genesis.Entities.Iam.OidcClientTokenLifetimes"] = "4BDF085E759254B9865BA70468702A66 B1D985892FBBBEAC57446D282F7F8477 B1D985892FBBBEAC57446D282F7F8477",
        ["Blocks.Genesis.Entities.Iam.Organization"] = "D9018E5E669B646EBD9A8CB0489F87E5 41BA6F34953C97FA626821402B6E19A8 B92381EDD35AD58658C0BF87B7900C4E",
        ["Blocks.Genesis.Entities.Iam.Permission"] = "D68EFCEE23E92222DFE1689B2649BE8E 290415232C6F6F3FE33E5DF0F5093DC9 333A50A7AB62C653ED58D1548E178F61",
        ["Blocks.Genesis.Entities.Iam.RefreshTokenModel"] = "895E7892AE56D1BA7FF642177FB81567 5C329018C53C23CE6881179442D44862 4DC936857F640DC4852C420C6BABCC83",
        ["Blocks.Genesis.Entities.Iam.Role"] = "10FFE69EF59B086F00EBFC0720358285 567AA88FDE028D244FD7E86758D1CAFE 47C99F71439756817AE1A2B4B387FF2F",
        ["Blocks.Genesis.Entities.Iam.Secret"] = "0864F1EFEE27F7C5F9215400B2C7D052 6B0CA0BD30D00F665FEE384D0560F53A 64471E22A0F2BE57767BFE5F70FD958E",
        ["Blocks.Genesis.Entities.Iam.SignupLink"] = "C26D6690D540F91749B06C1964DAA0E9 34F604BA2804B196BE23AA663977B497 CCD51B8A51F56807C45AE81230B4577C",
        ["Blocks.Genesis.Entities.Iam.SignupLinkConfiguration"] = "B274E8642906F4586A167EA60EEB1EF5 898F62AE00AD1FD0C9C330F359F70C2C DF71CC955860A86E667BE6CEC63E0E75",
        ["Blocks.Genesis.Entities.Iam.TenantConfiguration"] = "4EF0B5173C186FFFF3E0B4652B48ADD6 433F6E2063FC00EBE979819C0CABEC5A 7AE0848372E0597F5977BCF22A4FC2D5",
        ["Blocks.Genesis.Entities.Iam.Theme"] = "A69D7F38B931C94488E5D0C0AC51FA30 E91BFA85DC0B2850755BA0A8AA016502 37C890EEE8840440E665795AE460D2C1",
        ["Blocks.Genesis.Entities.Iam.TokenRevocationModel"] = "F9703E68476977EBEFC6C0E11F453934 1DA5B0DB1E18DDE049E81940C32C6ADD 1DA5B0DB1E18DDE049E81940C32C6ADD",
        ["Blocks.Genesis.Entities.Iam.User"] = "D57111B5B68A34D5F80B131C09EE4EF0 5D5DCDA100D5CB6A8D7ADBFDD3CC37BD 3A31B548139502962003BA80DD0F37C9",
        ["Blocks.Genesis.Entities.Iam.UserActivity"] = "9C398DD7F02CB477CBE7055F5E9DFEBD 8303FA258A627C927327F5DC9469C15E 3A56DBB1940EADF9E2460D185974D87D",
        ["Blocks.Genesis.Entities.Iam.UserKeyMap"] = "D403E7E0E263E88EE52DFD72067C547A 56ABE72D8000A2E7BBA75D83AA50DCC4 2F8D9B864A86AD9409D2B0CD72284295",
        ["Blocks.Genesis.Entities.Iam.UserMfaEnrollment"] = "3F9B6E6FA7342DC449ACD83968C106A8 3E14672E4930C14339D5466D483940BA 7CCB31415576DF8A3BFBC94276085136",
        ["Blocks.Genesis.Entities.Iam.UserTotpDetail"] = "41963F1E1C3F8F65B71B0198FC7AEBD2 BCA9B0C39449EFC94B554705D564DF19 E65DC312B4F0717504492215E24C96A8",
    };

    public static TheoryData<string> DocumentClasses() => EntityAssertions.DocumentClassesOf("Iam");

    public static TheoryData<string> CatalogClasses() => EntityAssertions.CatalogClassesOf("iam");

    [Fact]
    public void GoldenBytes_HashForEveryClass() => EntityAssertions.HashesCoverEveryClass("Iam", ServiceClassBsonHashes);

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
    public void Permission_SupersetFields_NotStoredWhenNull_ReadWhenOsSetsThem_NeverInJson()
    {
        var p = new Blocks.Genesis.Entities.Iam.Permission { ItemId = "p1", Name = "n" };
        var doc = p.ToBsonDocument();
        Assert.False(doc.Contains("IsCaptchaRequired"));
        Assert.False(doc.Contains("IsMFARequired"));

        doc["IsCaptchaRequired"] = true; // os $set (ApiEndpointConfigRepository.UpdateAsync)
        doc["IsMFARequired"] = false;
        var read = BsonSerializer.Deserialize<Blocks.Genesis.Entities.Iam.Permission>(doc);
        Assert.True(read.IsCaptchaRequired);
        Assert.False(read.IsMFARequired);

        var json = JsonSerializer.Serialize(read);
        Assert.DoesNotContain("IsCaptchaRequired", json, StringComparison.Ordinal);
        Assert.DoesNotContain("IsMFARequired", json, StringComparison.Ordinal);
    }

    [Fact]
    public void IamUserEnums_KeepTheIntValuesOsAndLogicStore()
    {
        // os People/User.cs and logic Identifier/User.cs copies use these int values today (C18).
        Assert.Equal([0, 1, 2, 3, 4, 5], Enum.GetValues<Blocks.Genesis.Entities.Iam.UserCreationType>().Select(v => (int)v));
        Assert.Equal([0, 1, 2], Enum.GetValues<Blocks.Genesis.Entities.Iam.UserPassType>().Select(v => (int)v));
        Assert.Equal([0, 1, 2, 3, 4], Enum.GetValues<Blocks.Genesis.Entities.Iam.UserMfaType>().Select(v => (int)v));
        Assert.Equal([0, 1, 2, 3], Enum.GetValues<Blocks.Genesis.Entities.Iam.UserLogInType>().Select(v => (int)v));
    }
}
