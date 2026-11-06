using Blocks.Genesis.DataAccess;
using Blocks.Genesis.DataAccess.Rules;

namespace XUnitTest.DataAccess.Rules;

/// <summary>
/// The variable grammar shared by default values and validation rules. The client's
/// <c>utils/variable-syntax.test.ts</c> runs the same table, so both sides give the same message.
/// </summary>
public class VariableSyntaxTests
{
    private const string Schema = "Employee";

    private static readonly IReadOnlyDictionary<string, VariableField> Fields = new Dictionary<string, VariableField>
    {
        ["FirstName"] = new("String", false, false),
        ["LastName"] = new("String", false, false),
        ["Name"] = new("String", false, false),
        ["Age"] = new("Int", false, false),
        ["StartDate"] = new("DateTime", false, false),
        ["Address"] = new("Address", false, true),
        ["Address.City"] = new("String", false, false),
    };

    private static string? Error(string text, VariableMode mode = VariableMode.Template, bool targetIsList = false)
    {
        var (parts, error) = VariableSyntax.Parse(text, mode, Schema);
        return (error ?? VariableSyntax.CheckReferences(parts, mode, Schema, Fields, targetIsList))?.Message;
    }

    [Theory]
    [InlineData("{{Employee.FirstName} {{Employee.LastName}}", "Unclosed variable at position 1: expected \"}}\"")]
    [InlineData("{Employee.FirstName}", "Single \"{\" at position 1: variables are written {{Employee.FirstName}}")]
    [InlineData("Hi }}", "Unexpected \"}}\" at position 4")]
    [InlineData("{{ }}", "Empty variable at position 1")]
    [InlineData("{{Employee.First Name}}", "Invalid name \"Employee.First Name\" at position 1: use letters, digits, \"_\" and \".\" between parts")]
    [InlineData("{{FirstName}}", "Missing prefix at position 1: write {{Employee.FirstName}} or {{Auth.…}}")]
    [InlineData("{{Department.Name}}", "Unknown prefix \"Department\" at position 1: use Employee or Auth")]
    [InlineData("{{Employee.FirstNme}}", "Unknown property \"Employee.FirstNme\" at position 1 (did you mean Employee.FirstName?)")]
    [InlineData("{{Employee.Address}}", "\"Employee.Address\" holds an object at position 1: use one of its properties, e.g. {{Employee.Address.City}}")]
    [InlineData("{{Auth.Mail}}", "Unknown value \"Auth.Mail\" at position 1: use Auth.UserId, Auth.Email, Auth.Roles, Auth.Permissions, Auth.OrganizationId")]
    [InlineData("{{Auth.Roles}}", "Auth.Roles is a list at position 1: use it in a list property")]
    [InlineData("Dear {{Employee.FirstName}", "Unclosed variable at position 6: expected \"}}\"")]
    public void Template_SyntaxErrors(string text, string expected)
    {
        Assert.Equal(expected, Error(text));
    }

    [Theory]
    [InlineData("Hi {{Employee.Age}}", "A validation value is either a fixed value or one variable, e.g. {{Employee.Age}}")]
    [InlineData("{{Auth.UserId}}", "Auth values aren't available in validation rules at position 1: use an access policy")]
    [InlineData("{{StartDate}}", "Missing prefix at position 1: write {{Employee.StartDate}}")]
    [InlineData("{{Department.Name}}", "Unknown prefix \"Department\" at position 1: use Employee")]
    public void SingleVariable_Errors(string text, string expected)
    {
        Assert.Equal(expected, Error(text, VariableMode.SingleVariable));
    }

    [Theory]
    [InlineData("{{Employee.FirstName}} {{Employee.LastName}}")]
    [InlineData("{{ Auth.UserId }}")]
    [InlineData("{{auth.userid}}")]
    [InlineData("{{Employee.Address.City}}")]
    [InlineData("Plain text, no variables")]
    [InlineData("")]
    public void Template_Valid(string text)
    {
        Assert.Null(Error(text));
    }

    [Fact]
    public void AuthList_IsAllowedInAListProperty()
    {
        Assert.Null(Error("{{Auth.Roles}}", targetIsList: true));
    }

    [Fact]
    public void Parse_SplitsTextAndVariables_WithPositions()
    {
        var (parts, error) = VariableSyntax.Parse("Dear {{Employee.FirstName}}!", VariableMode.Template, Schema);

        Assert.Null(error);
        Assert.Equal(3, parts.Count);
        Assert.Equal("Dear ", parts[0].Text);
        Assert.Equal(("Employee", "FirstName", 6), (parts[1].Prefix, parts[1].Path, parts[1].Position));
        Assert.Equal("!", parts[2].Text);
    }

    [Fact]
    public void Resolve_ReadsInputThenStoredRecord_AndAuthThroughThePolicyResolver()
    {
        var input = new Dictionary<string, object?> { ["FirstName"] = "Ann", ["Address"] = new Dictionary<string, object?> { ["City"] = "Dhaka" } };
        var stored = new Dictionary<string, object?> { ["StartDate"] = new DateTime(2025, 1, 10) };
        var principal = new AccessPrincipal("u1", "ann", "t1", "o1", ["admin"], [], IsAuthenticated: true, IsRootTenant: false) { HasContext = true };

        VariablePart V(string name) => VariableSyntax.Parse($"{{{{{name}}}}}", VariableMode.Template, Schema).Parts[0];

        Assert.Equal("Ann", VariableSyntax.Resolve(V("Employee.FirstName"), input, stored, principal));
        Assert.Equal("Dhaka", VariableSyntax.Resolve(V("Employee.Address.City"), input, stored, principal));
        Assert.Equal(new DateTime(2025, 1, 10), VariableSyntax.Resolve(V("Employee.StartDate"), input, stored, principal));
        Assert.Null(VariableSyntax.Resolve(V("Employee.LastName"), input, stored, principal));
        Assert.Equal("u1", VariableSyntax.Resolve(V("Auth.UserId"), input, stored, principal));
        Assert.Null(VariableSyntax.Resolve(V("Auth.UserId"), input, stored, null));
    }

    [Theory]
    [InlineData("{{Employee.StartDate}}", true)]
    [InlineData("{{ Employee.StartDate }}", true)]
    [InlineData("2025-01-01", false)]
    [InlineData("Hi {{Employee.StartDate}}", false)]
    [InlineData(null, false)]
    public void TryGetSingleVariable(string? value, bool expected)
    {
        Assert.Equal(expected, VariableSyntax.TryGetSingleVariable(value, out _));
    }
}
