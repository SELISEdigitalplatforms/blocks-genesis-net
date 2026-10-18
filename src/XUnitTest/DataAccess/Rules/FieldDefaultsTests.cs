using Blocks.Genesis.DataAccess;
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.DataAccess.Rules;
using Blocks.Genesis.Entities.Data;

namespace XUnitTest.DataAccess.Rules;

/// <summary>Default values: what is filled on insert (use cases UC1–UC9, UC14) and what may be saved.</summary>
public class FieldDefaultsTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);
    private static readonly AccessPrincipal User1 =
        new("u1", "ann", "t1", "o1", ["admin", "hr"], [], IsAuthenticated: true, IsRootTenant: false) { HasContext = true };

    private static FieldDefinitionResponse F(string name, string type = "String", bool isArray = false, FieldDefaultValue? dv = null, List<FieldDefinitionResponse>? children = null) =>
        new() { Name = name, Type = type, IsArray = isArray, DefaultValue = dv, Fields = children ?? [] };

    private static FieldDefaultValue Static(object? value) => new() { Kind = DefaultValueKind.Static, Value = value };
    private static FieldDefaultValue Template(object value) => new() { Kind = DefaultValueKind.Template, Value = value };
    private static FieldDefaultValue NowPlus(int days = 0) => new() { Kind = DefaultValueKind.Now, Offset = days == 0 ? null : new DefaultValueOffset { Days = days } };

    private static readonly List<FieldDefinitionResponse> Employee =
    [
        F("FirstName"),
        F("LastName"),
        F("Tags", isArray: true, dv: Static(new object[] { "employee" })),
        F("DisplayName", dv: Template("{{Employee.FirstName}} {{Employee.LastName}}")),
        F("JoiningDate", "DateTime", dv: NowPlus()),
        F("HrYearRenewDate", "DateTime", dv: NowPlus(365)),
        F("Score", "Int", dv: Static(5L)),
        F("Read", "OperationAccess", children: [F("Roles", isArray: true, dv: Static(new List<object> { "admin", "app-user" }))]),
        F("Edit", "OperationAccess", children: [F("UserIds", isArray: true, dv: Template(new object[] { "{{Auth.UserId}}" }))]),
        F("Owners", "OperationAccess", children: [F("Roles", isArray: true, dv: Template(new object[] { "{{Auth.Roles}}" }))]),
    ];

    private static Dictionary<string, object?> Insert(Dictionary<string, object?> input, AccessPrincipal? principal = null)
    {
        var defaults = FieldDefaultsApplier.Compute(input, "Employee", Employee, principal ?? User1, Now);
        FieldDefaultsApplier.Apply(input, defaults);
        return input;
    }

    [Fact]
    public void FillsEveryKind_WhenNothingIsSent()
    {
        var row = Insert(new() { ["FirstName"] = "Ann", ["LastName"] = "Lee" });

        Assert.Equal(new List<object?> { "employee" }, row["Tags"]);                       // UC1
        Assert.Equal("Ann Lee", row["DisplayName"]);                                        // UC3
        Assert.Equal(Now, row["JoiningDate"]);                                              // UC8
        Assert.Equal(Now.AddDays(365), row["HrYearRenewDate"]);                             // UC9
        Assert.Equal(5, row["Score"]);                                                      // stored as a number, not "5"
        Assert.Equal(new List<object?> { "admin", "app-user" }, ((Dictionary<string, object?>)row["Read"]!)["Roles"]);   // UC6
        Assert.Equal(new List<object?> { "u1" }, ((Dictionary<string, object?>)row["Edit"]!)["UserIds"]);                  // UC7
        Assert.Equal(new List<object?> { "admin", "hr" }, ((Dictionary<string, object?>)row["Owners"]!)["Roles"]);         // a list token expands
    }

    [Fact]
    public void SentValues_Win_AndNullCountsAsMissing()
    {
        var row = Insert(new() { ["Tags"] = new List<object?> { "x" }, ["Score"] = null });

        Assert.Equal(new List<object?> { "x" }, row["Tags"]);                               // UC2
        Assert.Equal(5, row["Score"]);
    }

    [Fact]
    public void Template_MissingPartsBecomeEmpty_AndEmptyResultLeavesItUnset()
    {
        Assert.Equal("Ann", Insert(new() { ["FirstName"] = "Ann" })["DisplayName"]);       // UC4
        Assert.False(Insert(new())?.ContainsKey("DisplayName"));                            // UC5
    }

    [Fact]
    public void NoSignedInUser_AuthTokensAreEmpty()
    {
        var row = Insert(new() { ["Edit"] = null }, AccessPrincipal.Anonymous);

        Assert.False(row.TryGetValue("Edit", out var edit) && edit is Dictionary<string, object?> e && e.ContainsKey("UserIds"));
    }

    [Fact]
    public void ExistingParentObject_GetsOnlyTheMissingChild()
    {
        var row = Insert(new() { ["Read"] = new Dictionary<string, object?> { ["Roles"] = new List<object?> { "owner" } } });

        Assert.Equal(new List<object?> { "owner" }, ((Dictionary<string, object?>)row["Read"]!)["Roles"]);
    }

    [Fact]
    public void Apply_IsSafeToRepeat_AfterAccessRulesRemovedAValue()
    {
        var input = new Dictionary<string, object?> { ["FirstName"] = "Ann" };
        var defaults = FieldDefaultsApplier.Compute(input, "Employee", Employee, User1, Now);
        FieldDefaultsApplier.Apply(input, defaults);

        input.Remove("Tags");                       // e.g. column access stripped it
        input["DisplayName"] = "Kept";              // a value that is present is never overwritten
        FieldDefaultsApplier.Apply(input, defaults);

        Assert.Equal(new List<object?> { "employee" }, input["Tags"]);
        Assert.Equal("Kept", input["DisplayName"]);
    }

    [Fact]
    public void ASentValueRemovedByAccessRules_IsFilledByItsDefault()   // UC12
    {
        var input = new Dictionary<string, object?> { ["Tags"] = new List<object?> { "mine" } };
        var defaults = FieldDefaultsApplier.Compute(input, "Employee", Employee, User1, Now);
        FieldDefaultsApplier.Apply(input, defaults);
        Assert.Equal(new List<object?> { "mine" }, input["Tags"]);   // the sent value wins

        input.Remove("Tags");                                         // the caller may not write Tags
        FieldDefaultsApplier.Apply(input, defaults);
        Assert.Equal(new List<object?> { "employee" }, input["Tags"]);
    }

    [Fact]
    public void ListsOfObjects_DefaultsGoIntoEachSentItem_NoItemsAreCreated()
    {
        List<FieldDefinitionResponse> order = [F("Lines", "Line", isArray: true, children: [F("Qty", "Int", dv: Static(1L))])];
        var input = new Dictionary<string, object?>
        {
            ["Lines"] = new List<object?> { new Dictionary<string, object?>(), new Dictionary<string, object?> { ["Qty"] = 3 } },
        };

        FieldDefaultsApplier.Apply(input, FieldDefaultsApplier.Compute(input, "Order", order, User1, Now));

        var lines = (List<object?>)input["Lines"]!;
        Assert.Equal(1, ((Dictionary<string, object?>)lines[0]!)["Qty"]);
        Assert.Equal(3, ((Dictionary<string, object?>)lines[1]!)["Qty"]);
        Assert.Empty(FieldDefaultsApplier.Compute(new Dictionary<string, object?>(), "Order", order, User1, Now));
    }

    // ---- What may be saved ---------------------------------------------------------------------

    private static readonly IReadOnlyDictionary<string, VariableField> EmployeeFields = new Dictionary<string, VariableField>
    {
        ["FirstName"] = new("String", false, false),
        ["Age"] = new("Int", false, false),
    };

    private static string? Check(string name, string type, FieldDefaultValue dv, bool isArray = false, string[]? enumValues = null) =>
        FieldDefaultValueCheck.Check(name, type, isArray, enumValues, dv, "Employee", EmployeeFields);

    [Theory]
    [InlineData("Age", "Int", "abc", "\"abc\" is not a whole number")]
    [InlineData("Ratio", "Float", "x", "\"x\" is not a number")]
    [InlineData("Active", "Boolean", "maybe", "\"maybe\" is not true or false")]
    [InlineData("JoinedOn", "DateTime", "2025-13-01", "\"2025-13-01\" is not a valid date-time")]
    public void Static_WrongType(string name, string type, string value, string expected)
    {
        Assert.Equal(expected, Check(name, type, Static(value)));
    }

    [Fact]
    public void Static_EnumAndListShape()
    {
        Assert.Equal("\"Gold\" is not one of the listed values (Silver, Bronze)", Check("Tier", "Enum", Static("Gold"), enumValues: ["Silver", "Bronze"]));
        Assert.Null(Check("Tier", "Enum", Static("Silver"), enumValues: ["Silver", "Bronze"]));
        Assert.Equal("A list is expected for Tags[]", Check("Tags", "String", Static("employee"), isArray: true));
        Assert.Null(Check("Tags", "String", Static(new object[] { "employee" }), isArray: true));
    }

    [Fact]
    public void KindMustFitTheType_AndLockedOrUnsupportedPropertiesAreRefused()
    {
        Assert.Equal("Now can only be used on single date-time properties", Check("Name", "String", NowPlus()));
        Assert.Equal("Templates can only be used on text properties", Check("Age", "Int", Template("{{Employee.Age}}")));
        Assert.Equal("CreatedDate is set by the platform and can't have a default value", Check("CreatedDate", "DateTime", NowPlus()));
        Assert.Equal("Default values can't be set on GeoJson properties", Check("Location", "GeoJson", Static("x")));
        Assert.Null(Check("Tags", "String", Static(new object[] { "a" }), isArray: true)); // a system property that may have one
    }

    [Fact]
    public void Template_SyntaxErrorsAreReturned()
    {
        Assert.Equal("Unclosed variable at position 1: expected \"}}\"", Check("DisplayName", "String", Template("{{Employee.FirstName}")));
        Assert.Equal("Unknown property \"Employee.FirstNme\" at position 1 (did you mean Employee.FirstName?)", Check("DisplayName", "String", Template("{{Employee.FirstNme}}")));
        Assert.Null(Check("DisplayName", "String", Template("{{Employee.FirstName}} ({{Auth.Email}})")));
    }
}
