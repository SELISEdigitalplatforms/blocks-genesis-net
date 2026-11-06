using System.Reflection;
using Blocks.Genesis.DataAccess.Rules;
using Blocks.Genesis.Entities.Logic;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace XUnitTest.Entities.Logic;

/// <summary>
/// WorkflowEntity.Settings retyped from Dictionary&lt;string, string&gt; to WorkflowSettings (blocks-genesis-net#175).
/// Every stored shape is built by hand as BSON and read with the new type, alone and embedded in the two documents
/// that snapshot a workflow. The golden-byte rows for the changed classes are in LogicEntitiesTests.
/// </summary>
public class WorkflowSettingsCompatibilityTests
{
    // The Settings shapes that the Dictionary<string, string> property could have stored, plus the one incompatible shape.
    private const string Missing = "missing";
    private const string Null = "null";
    private const string Empty = "{}";
    private const string UnknownKeys = "{ timezone: UTC, anyKey: text }";
    private const string AlertNotADocument = "{ Alert: oops }";

    private static readonly string[] Legacy = [Missing, Null, Empty, UnknownKeys];

    public static TheoryData<string> LegacyShapes()
    {
        var data = new TheoryData<string>();
        foreach (var shape in Legacy)
            data.Add(shape);
        return data;
    }

    public static TheoryData<string, string> LegacyShapesInWrappers()
    {
        var data = new TheoryData<string, string>();
        foreach (var wrapper in new[] { nameof(WorkflowVersionEntity), nameof(WorkflowExecutionEntity) })
            foreach (var shape in Legacy)
                data.Add(wrapper, shape);
        return data;
    }

    [Fact]
    public void Types_HaveExactlyTheSpecMembersAndDefaults()
    {
        Assert.Equal(["Alert"], PublicProperties(typeof(WorkflowSettings)).Select(p => p.Name));
        Assert.Equal(typeof(WorkflowAlert), typeof(WorkflowSettings).GetProperty("Alert")!.PropertyType);
        Assert.Null(new WorkflowSettings().Alert);

        Assert.Equal(
            new Dictionary<string, Type> { ["IsEnabled"] = typeof(bool), ["Mode"] = typeof(WorkflowAlertMode), ["WorkflowId"] = typeof(string) },
            PublicProperties(typeof(WorkflowAlert)).ToDictionary(p => p.Name, p => p.PropertyType));
        var alert = new WorkflowAlert();
        Assert.Equal((true, WorkflowAlertMode.Workflow, (string?)null), (alert.IsEnabled, alert.Mode, alert.WorkflowId));

        var nullability = new NullabilityInfoContext();
        Assert.Equal(NullabilityState.Nullable, nullability.Create(typeof(WorkflowSettings).GetProperty("Alert")!).ReadState);
        Assert.Equal(NullabilityState.Nullable, nullability.Create(typeof(WorkflowAlert).GetProperty("WorkflowId")!).ReadState);

        Assert.Equal([("Workflow", 1)], Enum.GetValues<WorkflowAlertMode>().Select(m => (m.ToString(), (int)m)));

        Assert.All(new[] { typeof(WorkflowSettings), typeof(WorkflowAlert), typeof(WorkflowAlertMode) },
            t => Assert.Equal("Blocks.Genesis.Entities.Logic", t.Namespace));

        // No webhook mode or type yet: a later Webhook alert is purely additive. WebhookConfiguration is the schedules' type.
        Assert.Equal([nameof(WebhookConfiguration)], typeof(WorkflowEntity).Assembly.GetTypes()
            .Where(t => t.IsPublic && t.Namespace == "Blocks.Genesis.Entities.Logic" && t.Name.Contains("Webhook", StringComparison.Ordinal))
            .Select(t => t.Name));
    }

    [Fact]
    public void WorkflowEntity_DeclaresTypedNullableSettings_DefaultNotNull()
    {
        var property = typeof(WorkflowEntity).GetProperty(nameof(WorkflowEntity.Settings))!;

        Assert.Equal(typeof(WorkflowSettings), property.PropertyType);
        Assert.Equal(NullabilityState.Nullable, new NullabilityInfoContext().Create(property).ReadState);
        Assert.NotNull(property.GetSetMethod());
        Assert.NotNull(NewWorkflow().Settings);
    }

    [Fact]
    public void DefaultWorkflow_WritesSettingsWithANullAlert()
    {
        // Was "Settings: {}" with the dictionary.
        Assert.Equal(new BsonDocument("Alert", BsonNull.Value), NewWorkflow().ToBsonDocument()["Settings"]);
    }

    [Theory]
    [MemberData(nameof(LegacyShapes))]
    public void Legacy_SettingsShape_LoadsWithNoAlert(string shape)
    {
        var workflow = BsonSerializer.Deserialize<WorkflowEntity>(WorkflowDocument(shape));

        AssertLoadedAsLegacy(shape, workflow.Settings);
    }

    [Theory]
    [MemberData(nameof(LegacyShapesInWrappers))]
    public void Legacy_SettingsShape_EmbeddedInVersionAndExecution_Loads(string wrapper, string shape)
    {
        var snapshot = DeserializeSnapshot(wrapper, Wrap(wrapper, WorkflowDocument(shape)));

        Assert.Equal("wf-a", snapshot.Name);
        AssertLoadedAsLegacy(shape, snapshot.Settings);
    }

    [Fact]
    public void NullSettings_IsWrittenAsBsonNull_AndReadsBackAsNull()
    {
        var workflow = NewWorkflow();
        workflow.Settings = null;
        var document = workflow.ToBsonDocument();

        Assert.Equal(BsonNull.Value, document["Settings"]);
        Assert.Null(BsonSerializer.Deserialize<WorkflowEntity>(document).Settings);
    }

    [Fact]
    public void IncompatibleShape_AlertNotADocument_ThrowsFormatException()
    {
        // The only stored shape the new type cannot read; the blocks-logic pre-deploy check relies on this exception.
        var document = WorkflowDocument(AlertNotADocument);

        Assert.Throws<FormatException>(() => BsonSerializer.Deserialize<WorkflowEntity>(document));
        Assert.Throws<FormatException>(() => DeserializeSnapshot(nameof(WorkflowVersionEntity), Wrap(nameof(WorkflowVersionEntity), document)));
        Assert.Throws<FormatException>(() => DeserializeSnapshot(nameof(WorkflowExecutionEntity), Wrap(nameof(WorkflowExecutionEntity), document)));
    }

    [Fact]
    public void FullyPopulatedSettings_RoundTripWithEqualValuesAndIdenticalBytes()
    {
        var workflow = NewWorkflow();
        workflow.Settings = new WorkflowSettings { Alert = new() { IsEnabled = false, Mode = WorkflowAlertMode.Workflow, WorkflowId = "wf-b" } };
        var bytes = workflow.ToBson();

        Assert.Equal(
            new BsonDocument("Alert", new BsonDocument { { "IsEnabled", false }, { "Mode", 1 }, { "WorkflowId", "wf-b" } }),
            BsonSerializer.Deserialize<BsonDocument>(bytes)["Settings"]);
        var back = BsonSerializer.Deserialize<WorkflowEntity>(bytes);

        Assert.Equal((false, WorkflowAlertMode.Workflow, "wf-b"), (back.Settings!.Alert!.IsEnabled, back.Settings.Alert.Mode, back.Settings.Alert.WorkflowId));
        Assert.Equal(bytes, back.ToBson());
    }

    [Fact]
    public void AlertMode_IsStoredAsInt32()
    {
        var alert = new WorkflowSettings { Alert = new() { Mode = WorkflowAlertMode.Workflow } }.ToBsonDocument()["Alert"];

        Assert.Equal(new BsonInt32(1), alert["Mode"]);
    }

    [Fact]
    public void FutureAlertFields_AreIgnored_KnownFieldsLoad()
    {
        // Written by a later Genesis: an unknown scalar, and a Webhook sub-document for a future alert mode.
        var settings = new BsonDocument("Alert", new BsonDocument
        {
            { "IsEnabled", false },
            { "Mode", 1 },
            { "WorkflowId", "wf-b" },
            { "RetryCount", 3 },
            { "Webhook", new BsonDocument("Url", "https://example.invalid/hook") },
        });

        var alert = BsonSerializer.Deserialize<WorkflowSettings>(settings).Alert!;

        Assert.Equal((false, WorkflowAlertMode.Workflow, "wf-b"), (alert.IsEnabled, alert.Mode, alert.WorkflowId));
    }

    [Fact]
    public void FutureAlertMode_LoadsAsAnUndefinedValue()
    {
        // 2 is the spec's example (a later Webhook mode); change it to an unused value when that mode is added.
        var settings = new BsonDocument("Alert", new BsonDocument { { "IsEnabled", true }, { "Mode", 2 }, { "WorkflowId", BsonNull.Value } });

        var mode = BsonSerializer.Deserialize<WorkflowSettings>(settings).Alert!.Mode;

        Assert.Equal(2, (int)mode);
        Assert.False(Enum.IsDefined(mode));
    }

    [Fact]
    public void NullWorkflowId_RoundTripsAsNull()
    {
        var document = new WorkflowSettings { Alert = new() { WorkflowId = null } }.ToBsonDocument();

        Assert.Equal(BsonNull.Value, document["Alert"]["WorkflowId"]);
        Assert.Null(BsonSerializer.Deserialize<WorkflowSettings>(document).Alert!.WorkflowId);
    }

    [Theory]
    [InlineData(typeof(WorkflowSettings))]
    [InlineData(typeof(WorkflowAlert))]
    public void NewClasses_IgnoreAnExtraField(Type type)
    {
        Assert.NotNull(type.GetCustomAttribute<BsonIgnoreExtraElementsAttribute>(inherit: false));

        var document = Activator.CreateInstance(type)!.ToBsonDocument(type);
        document["SomeFieldNobodyKnows"] = 42;

        Assert.Null(Record.Exception(() => BsonSerializer.Deserialize(document, type)));
    }

    [Fact]
    public void WorkflowEntity_ExposesNoSettingsRulePaths()
    {
        var rules = SystemSchemaRules.For(nameof(WorkflowEntity))!;

        Assert.Equal(RuleLevel.Normal, rules.Level);
        Assert.DoesNotContain(rules.Fields.Keys, path => path.StartsWith("Settings", StringComparison.Ordinal));
    }

    private static IEnumerable<PropertyInfo> PublicProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(p => p.Name, StringComparer.Ordinal);

    private static WorkflowEntity NewWorkflow() => new() { Name = "wf-a", TenantId = "t1" };

    /// <summary>A WorkflowEntity document whose Settings element has the given stored shape.</summary>
    private static BsonDocument WorkflowDocument(string shape)
    {
        var document = NewWorkflow().ToBsonDocument();
        switch (shape)
        {
            case Missing: document.Remove("Settings"); break;
            case Null: document["Settings"] = BsonNull.Value; break;
            case Empty: document["Settings"] = new BsonDocument(); break;
            case UnknownKeys: document["Settings"] = new BsonDocument { { "timezone", "UTC" }, { "anyKey", "text" } }; break;
            case AlertNotADocument: document["Settings"] = new BsonDocument("Alert", "oops"); break;
            default: throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
        return document;
    }

    private static void AssertLoadedAsLegacy(string shape, WorkflowSettings? settings)
    {
        if (shape == Null)
        {
            Assert.Null(settings);
            return;
        }
        Assert.NotNull(settings);
        Assert.Null(settings.Alert);
    }

    private static BsonDocument Wrap(string wrapper, BsonDocument workflow)
    {
        BsonDocument document;
        string element;
        if (wrapper == nameof(WorkflowVersionEntity))
        {
            document = new WorkflowVersionEntity { WorkflowId = "w1", TenantId = "t1", Name = "v1", Snapshot = NewWorkflow() }.ToBsonDocument();
            element = nameof(WorkflowVersionEntity.Snapshot);
        }
        else
        {
            document = new WorkflowExecutionEntity { Id = "e1", TenantId = "t1", WorkflowId = "w1", WorkflowName = "wf", WorkflowSnapshot = NewWorkflow(), TriggerMetadata = new() }.ToBsonDocument();
            element = nameof(WorkflowExecutionEntity.WorkflowSnapshot);
        }
        document[element] = workflow;
        return document;
    }

    private static WorkflowEntity DeserializeSnapshot(string wrapper, BsonDocument document) => wrapper == nameof(WorkflowVersionEntity)
        ? BsonSerializer.Deserialize<WorkflowVersionEntity>(document).Snapshot
        : BsonSerializer.Deserialize<WorkflowExecutionEntity>(document).WorkflowSnapshot;
}
