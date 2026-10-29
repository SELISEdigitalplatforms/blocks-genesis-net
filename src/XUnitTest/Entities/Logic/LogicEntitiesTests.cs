using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Blocks.Genesis;
using Blocks.Genesis.DataAccess.Rules;
using Blocks.Genesis.Entities.Logic;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace XUnitTest.Entities.Logic;

/// <summary>Package classes moved from blocks-logic (genesis SPEC1 6.4, folder Logic/).</summary>
public class LogicEntitiesTests
{
    // 3 x SHA-256 (first 16 bytes, hex): filled sample, default instance, all-null instance, of the BSON that the blocks-logic SERVICE class wrote
    // (Common/EntitySamples: Filled, DefaultBson, NullBson), taken before the move from the feature/collection-cut working tree, 2026-10-13.
    // The package class must write exactly these bytes. A changed hash = a document change: owner decision, not a hash update.
    private static readonly Dictionary<string, string> ServiceClassBsonHashes = new(StringComparer.Ordinal)
    {
        // Filled-sample hashes of every BaseEntity class changed on purpose 2026-10-14 (owner decision): BaseEntity gained
        // nullable Create/Read/Update/Delete access lists ([BsonIgnoreIfNull]); default and all-null bytes are unchanged.
        ["Blocks.Genesis.Entities.Logic.EdgeEnity"] = "E3A0B446AEC8C66E427C0D39851CD22C 7CFEBFB731F3FA25FFEDDEF5057C783B 7CFEBFB731F3FA25FFEDDEF5057C783B",
        ["Blocks.Genesis.Entities.Logic.EndpointAccessPolicy"] = "58FBC5F681421C9D2423DB6315EF875C 4896F2EB22CF87A143FBA4E162841CBD 375FE8059C65A8FD067B671CED3C17E4",
        ["Blocks.Genesis.Entities.Logic.EndpointAccessRule"] = "B9AA7D3E2C099CD77FD2FD311C9568C3 2488C936126F4FC497D5A20BEDA41891 FA2407F5BA3CE363C23177B6403D0991",
        // FirebasePushConfiguration: new 2026-10-28, a key-value value (key firebase-config), not a collection.
        ["Blocks.Genesis.Entities.Logic.FirebasePushConfiguration"] = "E5E2B31048A647673EEE676E6738684B EACF51AEAF78950C842DB66B79442B18 1B982ECBCA0CE559FA16985FD4DCA592",
        ["Blocks.Genesis.Entities.Logic.FirebaseConfiguration"] = "E83EB0A371986AA26AAAF64822F74C8A F372E622A1002A315C18E521655B638E 0D2948EC208DE68BF530458BEB474736",
        ["Blocks.Genesis.Entities.Logic.FunctionAuditEventEntity"] = "B9D5893D69C28394E69EC21D835BE069 0221ED0E62E9651C0B14132D83744BBB 174C63737407D962058F53B22F9F4FDA",
        ["Blocks.Genesis.Entities.Logic.FunctionBuildEntity"] = "8DAB71F20FE1DAD5EE03C1C70DBC2C7C E7E552FD128EFCE5E3F4FEF98D4D6330 E8C6D317D52DF1876AFB4824410B7F5D",
        ["Blocks.Genesis.Entities.Logic.FunctionDeletion"] = "9B46951E2F772BCF10548DE712C8A820 893E6D0A851141148595DCF58120B81A 893E6D0A851141148595DCF58120B81A",
        ["Blocks.Genesis.Entities.Logic.FunctionEntity"] = "80B99B759F7F18ECAAACC119EAC2D37A B1FB78BE73167615FCD9CE031422553A F5DEF68B5301AA494A77C86B727F964B",
        ["Blocks.Genesis.Entities.Logic.FunctionLimits"] = "7D94E8C04785408E09BA3295B2BEA604 ADA1E27BC4BB40004B84F7BE3D9B5BC7 ADA1E27BC4BB40004B84F7BE3D9B5BC7",
        ["Blocks.Genesis.Entities.Logic.FunctionRunEntity"] = "490FCCFB36924E8689C077F7A406B3E2 638DADE505D0064DF680C01C78302BED 700B0F6B73AFDD643CF91B11D92B9B2C",
        ["Blocks.Genesis.Entities.Logic.FunctionRunLogEntity"] = "8CF02969169C937AB78494298BC80A1B B4C9E19C96E8875B46A83BB59EAD5B69 C289BB52BBA7E089DC631FF109D1F522",
        ["Blocks.Genesis.Entities.Logic.FunctionSource"] = "6ED0E106789AF2F0CB30A4F292F67D49 1512E157B597D6B760BC88F591B08951 1D85A7E556A886414160C9E35197E462",
        ["Blocks.Genesis.Entities.Logic.FunctionVersionEntity"] = "FD90B1D95A8E6159F8804777972B4E50 B694A2634134793982AE151B19C96BE8 A883ECEFB7FF79F0534BEA4AC4453754",
        ["Blocks.Genesis.Entities.Logic.Handle"] = "3CF14937A9BE619BE8EA3C3169722547 CFBA923EB5C9E3A3865F6BB22B5B95F0 B5D6C6159A9F469B4E927751ADA3FD00",
        ["Blocks.Genesis.Entities.Logic.MailBoxEntity"] = "5A4B760AFEACA87D70EEF6721CC43B81 7F36D7AD537519D477648E7D37EB3B36 834331064FA32D1991F29812F653C62C",
        ["Blocks.Genesis.Entities.Logic.MailBoxSyncCursor"] = "8F4E7FAEC8B2AC051D50FA2F26C0E033 B9E7B4311FDDA29830742B5A830DD9C8 442A6F2C18550B080DC3C5833CC63DBF",
        ["Blocks.Genesis.Entities.Logic.NodeEntity"] = "14F629649EEAD08939439BB99B09A5F9 DABAA20032E08940B6422BF75C3260DA 8B6F35C1BA9BD2C8330F1FBC1551BFFE",
        ["Blocks.Genesis.Entities.Logic.NodeExecutionEntity"] = "DC064FABF5D162465C3E11057EEE4C71 1627B11B3101EC4AB853AEB703AEBF39 0112286DC786A8DB0A7DC4177F3A3708",
        ["Blocks.Genesis.Entities.Logic.NodeOutputItemData"] = "38255AE4210B7D322C3328779776B8F9 728B090606A24593872CFD28837F7E1A 1681E01B1298378FDCDAE332952E26D1",
        ["Blocks.Genesis.Entities.Logic.NotificationConnection"] = "C9F08E37C7E15EAD5FDC2B2897C0498D C329B5859A6B3DEAFBE8DB1CE5F4D3BD C329B5859A6B3DEAFBE8DB1CE5F4D3BD",
        ["Blocks.Genesis.Entities.Logic.NotificationSubscription"] = "D6BA9495E4990A17E219B379FB2113A7 ED0C230AEE154A394BF7D3C229D1B0CA 4C74B7A2A729EB07BF61D87BB5B5AFA8",
        // Filled-sample hash changed on purpose 2026-10-28 (owner decision, notification spec 2): TenantId and OrganizationId,
        // [BsonIgnoreIfNull], so default and all-null bytes are unchanged.
        ["Blocks.Genesis.Entities.Logic.OfflineNotification"] = "B69DE9EB7EE22825B259E4D4190FBE47 E2757A89C88D5269FE31B7715166F54A E2757A89C88D5269FE31B7715166F54A",
        ["Blocks.Genesis.Entities.Logic.OutputAction"] = "E0040128E57021997C243E8F8860475D EA331C480FC1ABC2904DEE0A2826850D 0B750A374EDB03C96AB29D410AF4F766",
        ["Blocks.Genesis.Entities.Logic.OutputActionResult"] = "EA1514CCEE2B5C336A2BB7BBAFA5A411 E43D3EFBFEEB85D82ECCA50732910393 807D36C6EBF2AB077F86A7F7982829CA",
        ["Blocks.Genesis.Entities.Logic.PayloadData"] = "94E1135B6CB5DB372D1907801E0E67D4 5B35D8620C72489989B32C604C816650 5B35D8620C72489989B32C604C816650",
        ["Blocks.Genesis.Entities.Logic.Position"] = "8FA59EFA5F5B856E5BA0DFE4D4C4643B 2B5F1F23E78990E168D8392B62355B2E 2B5F1F23E78990E168D8392B62355B2E",
        ["Blocks.Genesis.Entities.Logic.ProxyBreakerConfig"] = "D51915F7FB0E71633CC653397FA2C9A0 0C8F4119D121F69D536289C47D723263 0C8F4119D121F69D536289C47D723263",
        ["Blocks.Genesis.Entities.Logic.ProxyConfigSnapshot"] = "3E101E905CB218D5EFF97BEE9F863375 9B8ABC4974E8FBCB943A2F006FB38F99 B32C6291CC89D883B1BB9919F6FF3D5E",
        ["Blocks.Genesis.Entities.Logic.ProxyDetailEntity"] = "E59C8DB65AD4C632F84EB08FA300478A 88AB535B200F26A3F086F5BE99924D86 5AA6B64723969A65C0162CFF3EE0D204",
        ["Blocks.Genesis.Entities.Logic.ProxyExecutionEntity"] = "D93BFA5CABE73D971931B0DB33503256 79470BB08079E4FB1AA53D3E3A8373A9 652F66A346F186D65A5BBB825D3FA2E0",
        ["Blocks.Genesis.Entities.Logic.ProxyFieldChange"] = "96DB2F21DE651116E0333784596C0D96 59BBCB90E8A828C44A2477CE0F7ABF7D BDEA51E0D6ADE93A3E2FF10B759E9A6C",
        ["Blocks.Genesis.Entities.Logic.ProxyKeyValue"] = "B8DB9126E81195DE06027308CBD49E9B BF3F990A275E91C087A2F95CCD000700 C22BE87E8EDF778B1095CCA3840CF24D",
        ["Blocks.Genesis.Entities.Logic.ProxyMethodConfig"] = "E65E0F4BA2D856927EA5F9D51C847588 32848642EEF83220556DA95CE3DA45C1 32848642EEF83220556DA95CE3DA45C1",
        ["Blocks.Genesis.Entities.Logic.ProxyResilienceConfig"] = "DD4F8FB80120009B93A59D13DD1D1EF9 05040EA592026CD908C91ACEDE6844EA 05040EA592026CD908C91ACEDE6844EA",
        ["Blocks.Genesis.Entities.Logic.ProxyRetryConfig"] = "A250A4EAB81A887DD01A798D3625B039 BF49229E18DCCB0AC50237C5291B02C1 BF49229E18DCCB0AC50237C5291B02C1",
        ["Blocks.Genesis.Entities.Logic.ProxyRouteConfig"] = "4E4E4BB523E9C496DC2E617A01516901 3C02C21C6ABE26FE29F7A507D4C5F4EB F065F57C455C715D85FDAF62F79A23D9",
        ["Blocks.Genesis.Entities.Logic.ProxyStats"] = "D1F5E98CECE127BC0A50716B691E7909 946060958FD73BDDE6F38057326B09A8 CCA3016383BB87AAC58B94234914B59A",
        ["Blocks.Genesis.Entities.Logic.ProxyStatsBucket"] = "EC36F2B27ED961C9A09AAD1B4D19796C BB81646D70608D6E89FEE2CA36697C73 BB81646D70608D6E89FEE2CA36697C73",
        ["Blocks.Genesis.Entities.Logic.ProxyVersionEntity"] = "BD5B16E500593522783367D28B648B15 34813CE0B9B3F1578B17E0D133E65E1F 43C1CB79586D929D49CB419A42DC5C7E",
        ["Blocks.Genesis.Entities.Logic.PublishedWorkflowMeta"] = "2C2F725A6B7057D8682676F5501D006B 79BE945308F28DD38A0209BDFFCC7902 EEF7286238F5C635A69D391882989F1C",
        ["Blocks.Genesis.Entities.Logic.QueueConfiguration"] = "9FE9FF8839F5A4AC38560A8EE3D07D71 CDCF639A858BDC9A4AE9153BF8FB79E3 BD7A8E34BED68304470F782D8CC3323B",
        ["Blocks.Genesis.Entities.Logic.RetryPolicy"] = "A42AD7F13D5CE25309B2A57BDE31DF93 3D7C42F557E6449A89C6C0B82D149BB4 3D7C42F557E6449A89C6C0B82D149BB4",
        ["Blocks.Genesis.Entities.Logic.RunAttempt"] = "C48EF91384B12532133DE5AD7DE06660 F8389498936B3142A7230FDFC7BED498 F8389498936B3142A7230FDFC7BED498",
        ["Blocks.Genesis.Entities.Logic.RunStage"] = "684374E1369D4FC1D40FAD267D8332D3 09A35D17994798A69F81EB8C0549F26E 03D6CDED6C2F625020FD69EA9A903D77",
        ["Blocks.Genesis.Entities.Logic.RunTiming"] = "220970D27C68C29377FC8687832514DB 3A469A49C14533113C19451E42160F01 73932D6042F76EBA1D9CDD38C7BE2D04",
        // Schedule filled hash changed on purpose 2026-10-26 (owner decision): optional TimeZone ([BsonIgnoreIfNull]); default and all-null bytes unchanged.
        ["Blocks.Genesis.Entities.Logic.Schedule"] = "F0F27477D293FA2D974DACD068B83A60 13670CA422BB0C8980180962D1A73648 E6858E525FDAAA7750D0866CDF847F3B",
        // ScheduleExecution is new in Genesis (2026-10-26), not moved from blocks-logic: these are its first hashes.
        ["Blocks.Genesis.Entities.Logic.ScheduleExecution"] = "46C0104220E4B1C1407650A278931201 F0B08A1FADB05BB7EAA51E3DF1A66DDF 85D0A1CF4D60C022A0DFC4D14A13B141",
        ["Blocks.Genesis.Entities.Logic.SubscriptionFilter"] = "37E7B716AAFA244FDE2A6D12381A782E 77BDD96714A7EA9E0D849051BB502744 B6C9739995C24894ECF7625D14D8B543",
        ["Blocks.Genesis.Entities.Logic.TestWorkflowMeta"] = "85DA441F3D289BEE89146204CAFDB134 684194B17B8083673D7E933AE299B0EC 667384C6537065C79BA6A9E8CDCC4C79",
        ["Blocks.Genesis.Entities.Logic.TriggerConfig"] = "3589F15317BDDBA2D219FD7ED1C212C9 EB462350FE6A2E12066344F913A11A3F 7C7E0435CFCFE9C9132A02315BDF40F0",
        ["Blocks.Genesis.Entities.Logic.TriggerMetadata"] = "EA9EDF5743960E72BEDD2EBAE5DA598E 46867C6C0BF5B7B63CDABD337BB3F731 46867C6C0BF5B7B63CDABD337BB3F731",
        ["Blocks.Genesis.Entities.Logic.VariableBinding"] = "B8DB9126E81195DE06027308CBD49E9B BF3F990A275E91C087A2F95CCD000700 C22BE87E8EDF778B1095CCA3840CF24D",
        ["Blocks.Genesis.Entities.Logic.WebhookConfiguration"] = "BF87D6B0647669CA2A05A788DDBFF03C 7DE415E28205F94102C601298C0D3F66 EB867AFD773E3A1A6CE0532E46C73393",
        // WorkflowAlert and WorkflowSettings are new in Genesis (2026-10-28, blocks-genesis-net#175), not moved from blocks-logic: these are their first hashes.
        ["Blocks.Genesis.Entities.Logic.WorkflowAlert"] = "8417690A27B3351BC5BE9AD391C6509A C902C069169F2CCCB10AA6D2D536F8B6 C902C069169F2CCCB10AA6D2D536F8B6",
        // Filled and default hashes changed on purpose 2026-10-28 (owner decision, blocks-genesis-net#175): Settings is a typed
        // WorkflowSettings instead of Dictionary<string, string>, so the default writes { Alert: null } instead of {}. All-null bytes are unchanged.
        ["Blocks.Genesis.Entities.Logic.WorkflowEntity"] = "22E7F2C1384B42DB974798A696596BF2 9A3C9658DD1D45BCEB8F9AAC629CF2E0 0202697A2132574273CFCBEDB09ADBFE",
        // Filled hash changed on purpose 2026-10-28 (owner decision, blocks-genesis-net#175): the WorkflowSnapshot has the typed
        // Settings. Default and all-null bytes are unchanged (the snapshot is null there).
        ["Blocks.Genesis.Entities.Logic.WorkflowExecutionEntity"] = "DD9E48CB68D1E6CADA143841E00A0700 0725477EEF14CA034523BA3ECD588C66 A4744968E7E6EDE70241D6329315C016",
        ["Blocks.Genesis.Entities.Logic.WorkflowItemExecutionEntity"] = "6F4AA65CDE19525DA3DDACD959C6C9A8 5F2D26C50FFB1F9C0B5039B0B80D2A61 7EF878EB0195BD49D3984E4707D3A05C",
        // WorkflowSettings: new 2026-10-28 (blocks-genesis-net#175), see WorkflowAlert above.
        ["Blocks.Genesis.Entities.Logic.WorkflowSettings"] = "414530681FDA47E892620BB3115CEA23 8515C07C6CECD8F26F62625EBD8A2D24 8515C07C6CECD8F26F62625EBD8A2D24",
        // Filled hash changed on purpose 2026-10-28 (owner decision, blocks-genesis-net#175): the Snapshot has the typed
        // Settings. Default and all-null bytes are unchanged (the snapshot is null there).
        ["Blocks.Genesis.Entities.Logic.WorkflowVersionEntity"] = "FB9891C17038B64AEBF7094E92C3E093 F6E0B59965DF31CB4420CE988EA2F3C6 E66BCC9278D19E1E7029ED97234A2F27",
    };

    public static TheoryData<string> DocumentClasses() => EntityAssertions.DocumentClassesOf("Logic");

    public static TheoryData<string> CatalogClasses() => EntityAssertions.CatalogClassesOf("logic");

    [Fact]
    public void GoldenBytes_HashForEveryClass() => EntityAssertions.HashesCoverEveryClass("Logic", ServiceClassBsonHashes);

    [Theory]
    [MemberData(nameof(DocumentClasses))]
    public void GoldenBytes_SameAsServiceClassBeforeTheMove(string fullName) => EntityAssertions.GoldenBytes(fullName, ServiceClassBsonHashes);

    [Theory]
    [MemberData(nameof(DocumentClasses))]
    public void ClassMap_Builds(string fullName) => EntityAssertions.ClassMapBuilds(fullName);

    [Theory]
    [MemberData(nameof(CatalogClasses))]
    public void CatalogEntity_NameBaseAndIgnoreExtraElements(string cls) => EntityAssertions.CatalogEntity(cls);

    // Schedule.TimeZone and ScheduleExecution (blocks-genesis-net#165). The golden-byte, class-map and catalog
    // theories above already cover the new class; these pin what they cannot see.

    [Fact]
    public void Schedule_TimeZone_RoundTripsWhenSet_OmittedWhenNull_OldDocumentReadsNull()
    {
        var withZone = new Schedule { Name = "s", TimeZone = "Asia/Dhaka" }.ToBsonDocument();
        Assert.Equal(new BsonString("Asia/Dhaka"), withZone["TimeZone"]);
        Assert.Equal("Asia/Dhaka", BsonSerializer.Deserialize<Schedule>(withZone).TimeZone);

        Assert.False(new Schedule { TimeZone = null }.ToBsonDocument().Contains("TimeZone"));

        // A document written before the property existed (same shape as the pre-change class) reads as UTC.
        var old = EntitySamples.Filled(typeof(PreTimeZoneSchedule)).ToBsonDocument(typeof(PreTimeZoneSchedule));
        Assert.False(old.Contains("TimeZone"));
        Assert.Null(BsonSerializer.Deserialize<Schedule>(old).TimeZone);
    }

    [Fact]
    public void Schedule_DocumentWithTimeZoneAndUnknownElement_ReadsIntoThePreChangeClass()
    {
        // The test-local class really is the pre-change shape: it writes the bytes Schedule writes without a TimeZone.
        var current = (Schedule)EntitySamples.Filled(typeof(Schedule));
        current.TimeZone = null;
        Assert.Equal(EntitySamples.ToBson(current, typeof(Schedule)),
            EntitySamples.ToBson(EntitySamples.Filled(typeof(PreTimeZoneSchedule)), typeof(PreTimeZoneSchedule)));
        Assert.Equal(EntitySamples.DefaultBson(typeof(Schedule)), EntitySamples.DefaultBson(typeof(PreTimeZoneSchedule)));
        Assert.Equal(EntitySamples.NullBson(typeof(Schedule)), EntitySamples.NullBson(typeof(PreTimeZoneSchedule)));

        var doc = ((Schedule)EntitySamples.Filled(typeof(Schedule))).ToBsonDocument();
        doc["TimeZone"] = "Asia/Dhaka";
        doc["SomeFieldNobodyKnows"] = 42;

        PreTimeZoneSchedule? read = null;
        Assert.Null(Record.Exception(() => read = BsonSerializer.Deserialize<PreTimeZoneSchedule>(doc)));
        Assert.Equal("Name-value", read!.Name);
    }

    [Fact]
    public void Schedule_Rules_UnchangedAtNormal_NoTimeZone()
    {
        var rules = SystemSchemaRules.For("Schedule")!;

        Assert.Equal(RuleLevel.Normal, rules.Level);
        string[] preChange =
        [
            "Name", "Description", "CronExpression", "StartDate", "EndDate", "Language", "Tags",
            .. new[] { "Create", "Read", "Update", "Delete" }.SelectMany(op => new[] { $"{op}.Roles", $"{op}.UserIds", $"{op}.Permissions" }),
        ];
        Assert.Equal(preChange.Order(StringComparer.Ordinal), rules.Fields.Keys.Order(StringComparer.Ordinal));
        Assert.Equal((RuleAccess.Blocked, RuleAccess.Blocked), (rules.ValidationFor("TimeZone"), rules.DefaultFor("TimeZone")));
    }

    [Fact]
    public void ScheduleExecution_ClosedToRules()
    {
        Assert.Null(typeof(ScheduleExecution).GetCustomAttribute<SystemSchemaRulesAttribute>());
        Assert.Null(SystemSchemaRules.For("ScheduleExecution"));
        Assert.DoesNotContain("ScheduleExecution", SystemSchemaRules.SchemaNames);
    }

    [Fact]
    public void ScheduleExecution_ExactPropertiesAndTypes_OnlyScheduleIdRequired_RestNullable()
    {
        var type = typeof(ScheduleExecution);
        Assert.True(type.IsSealed);
        Assert.Equal(typeof(BaseEntity), type.BaseType);
        Assert.Empty(type.GetNestedTypes());

        var declared = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).ToDictionary(p => p.Name);
        var expected = new Dictionary<string, Type>
        {
            ["ScheduleId"] = typeof(string),
            ["TenantId"] = typeof(string),
            ["ScheduleName"] = typeof(string),
            ["FiredUtc"] = typeof(DateTime?),
            ["FinishedUtc"] = typeof(DateTime?),
            ["Response"] = typeof(Dictionary<string, object>),
            ["Snapshot"] = typeof(string),
        };
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), declared.Keys.Order(StringComparer.Ordinal));
        Assert.All(expected, e => Assert.Equal(e.Value, declared[e.Key].PropertyType));

        // "new ScheduleExecution()" without ScheduleId fails to compile: the compiler enforces RequiredMemberAttribute.
        Assert.NotNull(type.GetCustomAttribute<RequiredMemberAttribute>());
        Assert.Equal(["ScheduleId"], declared.Values.Where(p => p.IsDefined(typeof(RequiredMemberAttribute))).Select(p => p.Name));
        Assert.Equal("ItemId", BsonClassMap.LookupClassMap(type).IdMemberMap!.MemberName);

        var nullability = new NullabilityInfoContext();
        Assert.Equal(NullabilityState.NotNull, nullability.Create(declared["ScheduleId"]).ReadState);
        Assert.All(declared.Values.Where(p => p.Name != "ScheduleId"),
            p => Assert.Equal(NullabilityState.Nullable, nullability.Create(p).ReadState));

        Assert.Equal("Status", ScheduleExecution.ResponseStatusKey);
        Assert.Equal("Body", ScheduleExecution.ResponseBodyKey);
    }

    [Fact]
    public void ScheduleExecution_Response_IntStatus_NullOmitted_UnknownKeyKept_NoDiscriminator()
    {
        // Filled: equal on read-back, Status an Int32 / int, Body a string, no "_t" at the root or inside Response.
        var fired = new DateTime(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);
        var filled = new ScheduleExecution
        {
            ItemId = "e1", ScheduleId = "s1", TenantId = "t1", ScheduleName = "Nightly",
            FiredUtc = fired, FinishedUtc = fired.AddSeconds(30), Snapshot = "{\"name\":\"s1\"}",
            Response = new() { [ScheduleExecution.ResponseStatusKey] = 504, [ScheduleExecution.ResponseBodyKey] = "Timed out: no response within 30 s." },
        };
        var doc = filled.ToBsonDocument();
        Assert.Equal(BsonType.Int32, doc["Response"]["Status"].BsonType);
        Assert.Equal(BsonType.String, doc["Response"]["Body"].BsonType);
        Assert.False(doc.Contains("_t"));
        Assert.False(doc["Response"].AsBsonDocument.Contains("_t"));
        // The bag must use AttributeBagSerializer: with the driver's permissive ObjectSerializer an object value
        // (e.g. a JsonElement) is written as { "_t": ... } and every later read of that row fails.
        Assert.IsType<Blocks.Genesis.Entities.Iam.AttributeBagSerializer>(BsonClassMap.LookupClassMap(typeof(ScheduleExecution)).GetMemberMap(nameof(ScheduleExecution.Response)).GetSerializer());

        var back = BsonSerializer.Deserialize<ScheduleExecution>(doc);
        Assert.Equal((filled.ItemId, filled.ScheduleId, filled.TenantId, filled.ScheduleName, filled.FiredUtc, filled.FinishedUtc, filled.Snapshot),
            (back.ItemId, back.ScheduleId, back.TenantId, back.ScheduleName, back.FiredUtc, back.FinishedUtc, back.Snapshot));
        Assert.Equal(filled.Response, back.Response);
        Assert.IsType<int>(back.Response![ScheduleExecution.ResponseStatusKey]);
        Assert.IsType<string>(back.Response[ScheduleExecution.ResponseBodyKey]);
        Assert.Equal(doc, back.ToBsonDocument());

        // No body: the key is simply absent.
        var noBody = new ScheduleExecution { ScheduleId = "s1", Response = new() { [ScheduleExecution.ResponseStatusKey] = 204 } }.ToBsonDocument();
        Assert.Equal(new BsonDocument("Status", 204), noBody["Response"]);
        Assert.Equal(new Dictionary<string, object> { ["Status"] = 204 }, BsonSerializer.Deserialize<ScheduleExecution>(noBody).Response);

        // Null Response: no element written, read back as null (not an empty bag); other nullables are BSON null.
        var minimal = new ScheduleExecution { ScheduleId = "s1" }.ToBsonDocument();
        Assert.False(minimal.Contains("Response"));
        Assert.All(new[] { "TenantId", "ScheduleName", "FiredUtc", "FinishedUtc", "Snapshot" }, name => Assert.Equal(BsonNull.Value, minimal[name]));
        Assert.Null(BsonSerializer.Deserialize<ScheduleExecution>(minimal).Response);

        // An unknown key inside the stored bag is kept; an unknown root element is ignored.
        minimal["Response"] = new BsonDocument { { "Status", 202 }, { "Attempt", 2 } };
        minimal["SomeFieldNobodyKnows"] = 42;
        var withExtra = BsonSerializer.Deserialize<ScheduleExecution>(minimal);
        Assert.Equal(new Dictionary<string, object> { ["Status"] = 202, ["Attempt"] = 2 }, withExtra.Response);
    }

    [Fact]
    public void ScheduleExecution_FilledWith1KbSnapshotAnd1KbBody_IsAtMost3Kb()
    {
        // Every member filled, except BaseEntity's per-record access lists: the Worker leaves BaseEntity at its defaults
        // (spec 6.3), where they are null and not written. With them filled too the sample is 3271 bytes.
        var sample = (ScheduleExecution)EntitySamples.Filled(typeof(ScheduleExecution));
        sample.Create = sample.Read = sample.Update = sample.Delete = null;
        sample.Snapshot = new string('s', 1024);
        sample.Response = new() { [ScheduleExecution.ResponseStatusKey] = 200, [ScheduleExecution.ResponseBodyKey] = new string('b', 1024) };

        var bytes = EntitySamples.ToBson(sample, typeof(ScheduleExecution));
        Assert.True(bytes.Length <= 3 * 1024, $"{bytes.Length} bytes");
    }

    /// <summary>Schedule as it was before TimeZone (dev @ 582c77ae): an older package reading a newer document.</summary>
    [BsonIgnoreExtraElements]
    public class PreTimeZoneSchedule : BaseEntity
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string Payload { get; set; } = null!;
        public string CronExpression { get; set; } = null!;
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public bool IsActive { get; set; } = true;
        public ScheduleKind Kind { get; set; } = ScheduleKind.Application;
        public ScheduleTriggerType TriggerType { get; set; } = ScheduleTriggerType.Queue;
        public WebhookConfiguration? Webhook { get; set; }
        public QueueConfiguration? Queue { get; set; }
    }
}
