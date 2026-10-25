using System.Text.Json;
using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.DataAccess.Rules;
using Blocks.Genesis.Entities.Data;

namespace XUnitTest.DataAccess.Rules;

/// <summary>
/// G1 (validation and access rules plan): rules without "only when" behave exactly as before the plan.
/// <c>validate-baseline.json</c> was recorded by running these cases on Genesis <c>origin/dev</c> at 6658c58, before any
/// change of the plan; every result must stay identical. Uses only APIs that existed then, so it can be re-recorded there:
/// set GENESIS_VALIDATE_BASELINE_OUT to a file path and run this test.
/// </summary>
public class ValidateBaselineTests
{
    private static readonly DateTime Jan10 = new(2025, 1, 10, 0, 0, 0, DateTimeKind.Utc);

    private static FieldDefinitionResponse Field(string name, string type, bool isArray = false, params ValidationRule[] rules) => new()
    {
        Name = name, Type = type, IsArray = isArray,
        ValidationRule = rules.Length == 0 ? null : new DataValidation { FieldName = name, Validations = [.. rules] },
    };

    private static ValidationRule R(ValidationType type, object? value = null, object? secondary = null, int version = 0, string? message = null, bool active = true) =>
        new() { Type = type, Value = value, SecondaryValue = secondary, EvaluationVersion = version, ErrorMessage = message, IsActive = active };

    private static SchemaDefinitionExtended Schema(params FieldDefinitionResponse[] fields) => new() { SchemaName = "Employee", Fields = [.. fields] };

    private sealed record Case(string Name, SchemaDefinitionExtended Schema, Dictionary<string, object?> Input, Dictionary<string, object?>? Stored = null);

    private static IEnumerable<Case> Cases()
    {
        // Every existing type on every value kind, versions 0 and 1.
        foreach (var value in new object?[] { "", "  ", null, "x" })
            yield return new($"NotEmpty v0 [{value ?? "null"}]", Schema(Field("Name", "String", false, R(ValidationType.NotEmpty))), new() { ["Name"] = value });
        yield return new("NotEmpty v0 with a Value (as Almora)", Schema(Field("Email", "String", false, R(ValidationType.NotEmpty, "^[^@]+@[^@]+$"))), new() { ["Email"] = "" });
        yield return new("Regex fails", Schema(Field("Code", "String", false, R(ValidationType.Regex, "^[0-9]+$"))), new() { ["Code"] = "abc" });
        yield return new("Regex passes", Schema(Field("Code", "String", false, R(ValidationType.Regex, "^[0-9]+$"))), new() { ["Code"] = "123" });
        foreach (var version in new[] { 0, 1 })
        {
            yield return new($"MinLength v{version}", Schema(Field("Name", "String", false, R(ValidationType.MinLength, 3, version: version))), new() { ["Name"] = "ab" });
            yield return new($"MaxLength v{version}", Schema(Field("Name", "String", false, R(ValidationType.MaxLength, 3L, version: version))), new() { ["Name"] = "abcd" });
            yield return new($"LengthRange v{version}", Schema(Field("Name", "String", false, R(ValidationType.LengthRange, 2, 4, version))), new() { ["Name"] = "a" });
        }
        foreach (var (type, value, bad, good) in new (string, object, object, object)[]
        {
            ("String", "DE", "AT", "DE"), ("Int", 5, 6, 5L), ("Float", 1.5, 2.0, 1.5), ("DateTime", Jan10, Jan10.AddDays(1), Jan10), ("Boolean", true, false, true),
        })
        {
            yield return new($"Equal {type} fails", Schema(Field("V", type, false, R(ValidationType.Equal, value))), new() { ["V"] = bad });
            yield return new($"Equal {type} passes", Schema(Field("V", type, false, R(ValidationType.Equal, value))), new() { ["V"] = good });
            yield return new($"NotEqual {type} fails", Schema(Field("V", type, false, R(ValidationType.NotEqual, value))), new() { ["V"] = good });
        }
        foreach (var (type, bound, value) in new (string, object, object)[] { ("Int", 10, 5), ("Float", 2.5, 2.6), ("DateTime", Jan10, "2025-01-09"), ("String", "m", "a") })
        {
            yield return new($"GreaterThan {type}", Schema(Field("V", type, false, R(ValidationType.GreaterThan, bound))), new() { ["V"] = value });
            yield return new($"LessThan {type}", Schema(Field("V", type, false, R(ValidationType.LessThan, bound))), new() { ["V"] = value });
            yield return new($"GreaterThanOrEqual {type}", Schema(Field("V", type, false, R(ValidationType.GreaterThanOrEqual, bound, version: 1))), new() { ["V"] = value });
            yield return new($"LessThanOrEqual {type}", Schema(Field("V", type, false, R(ValidationType.LessThanOrEqual, bound, version: 1))), new() { ["V"] = value });
        }
        yield return new("Range Int out", Schema(Field("Age", "Int", false, R(ValidationType.Range, 18, 65))), new() { ["Age"] = 70 });
        yield return new("Range Int in", Schema(Field("Age", "Int", false, R(ValidationType.Range, 18, 65, 1))), new() { ["Age"] = 30 });
        yield return new("Reference never fails here", Schema(Field("DeptId", "String", false, R(ValidationType.Reference, "Department", "ItemId", 1))), new() { ["DeptId"] = "x" });

        // Lists: version 0 checks the whole list, version 1 each item.
        var tags = new List<object?> { "ab", "abcdef", null };
        yield return new("List MaxLength v0 (whole list)", Schema(Field("Tags", "String", true, R(ValidationType.MaxLength, 3))), new() { ["Tags"] = tags });
        yield return new("List MaxLength v1 (each item)", Schema(Field("Tags", "String", true, R(ValidationType.MaxLength, 3, version: 1))), new() { ["Tags"] = tags });
        yield return new("List NotEmpty v1 empty", Schema(Field("Tags", "String", true, R(ValidationType.NotEmpty, version: 1))), new() { ["Tags"] = new List<object?>() });
        yield return new("List NotEmpty v0 empty", Schema(Field("Tags", "String", true, R(ValidationType.NotEmpty))), new() { ["Tags"] = new List<object?>() });

        // Variables (version 1), with and without the stored record; version 0 keeps the text.
        var dates = Schema(Field("StartDate", "DateTime"), Field("EndDate", "DateTime", false, R(ValidationType.GreaterThan, "{{Employee.StartDate}}", version: 1)));
        yield return new("Variable from input", dates, new() { ["StartDate"] = Jan10, ["EndDate"] = Jan10.AddDays(-1) });
        yield return new("Variable from stored (partial update)", dates, new() { ["EndDate"] = Jan10.AddDays(-1) }, new() { ["StartDate"] = Jan10 });
        yield return new("Variable with no value skips", dates, new() { ["EndDate"] = Jan10 });
        yield return new("Variable v0 stays text",
            Schema(Field("StartDate", "DateTime"), Field("EndDate", "DateTime", false, R(ValidationType.GreaterThan, "{{Employee.StartDate}}"))),
            new() { ["StartDate"] = Jan10, ["EndDate"] = Jan10.AddDays(1) });
        yield return new("Variable Range message",
            Schema(Field("StartDate", "DateTime"), Field("EndDate", "DateTime"), Field("Mid", "DateTime", false, R(ValidationType.Range, "{{Employee.StartDate}}", "{{Employee.EndDate}}", 1))),
            new() { ["StartDate"] = Jan10, ["EndDate"] = Jan10.AddDays(2), ["Mid"] = Jan10.AddDays(5) });

        // Nested objects, lists of objects, inactive and unknown.
        var address = new FieldDefinitionResponse { Name = "Address", Type = "Address", Fields = [Field("City", "String", false, R(ValidationType.MinLength, 3, version: 1))] };
        yield return new("Nested object", Schema(address), new() { ["Address"] = new Dictionary<string, object?> { ["City"] = "NY" } });
        var items = new FieldDefinitionResponse { Name = "Items", Type = "Item", IsArray = true, Fields = [Field("Qty", "Int", false, R(ValidationType.GreaterThan, 0, version: 1))] };
        yield return new("List of objects", Schema(items), new()
        {
            ["Items"] = new List<object?> { new Dictionary<string, object?> { ["Qty"] = 1 }, new Dictionary<string, object?> { ["Qty"] = 0 } },
        });
        yield return new("Inactive rule skipped", Schema(Field("Name", "String", false, R(ValidationType.NotEmpty, active: false))), new() { ["Name"] = "" });
        yield return new("Unknown field skipped", Schema(Field("Name", "String", false, R(ValidationType.NotEmpty))), new() { ["Other"] = "" });
        yield return new("Field not sent is not checked", Schema(Field("Name", "String", false, R(ValidationType.NotEmpty)), Field("Age", "Int")), new() { ["Age"] = 1 });
        yield return new("Custom message", Schema(Field("Name", "String", false, R(ValidationType.MaxLength, 2, message: "Too long"))), new() { ["Name"] = "abc" });
        yield return new("Several rules, several errors",
            Schema(Field("Name", "String", false, R(ValidationType.MinLength, 5), R(ValidationType.Regex, "^[0-9]+$"), R(ValidationType.NotEqual, "ab"))),
            new() { ["Name"] = "ab" });
    }

    private static Dictionary<string, List<string>> Run() =>
        Cases().ToDictionary(c => c.Name, c => c.Input.Validate(c.Schema, c.Stored).Errors
            .Select(e => $"{e.FieldName} | {e.ValidationType} | {e.Message}").ToList());

    [Fact]
    public void RulesWithoutACondition_GiveTheSameResultsAsBeforeThePlan()
    {
        var results = Run();
        var outFile = Environment.GetEnvironmentVariable("GENESIS_VALIDATE_BASELINE_OUT");
        if (!string.IsNullOrEmpty(outFile))
        {
            System.IO.File.WriteAllText(outFile, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        var baseline = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
            System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DataAccess", "Rules", "validate-baseline.json")))!;
        Assert.Equal(baseline.Keys.Order(), results.Keys.Order());
        foreach (var (name, errors) in baseline)
            Assert.True(errors.SequenceEqual(results[name]), $"{name}: expected [{string.Join("; ", errors)}], got [{string.Join("; ", results[name])}]");
    }
}
