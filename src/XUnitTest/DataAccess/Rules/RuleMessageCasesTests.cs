using System.Reflection;
using System.Text.Json;
using Blocks.Genesis.DataAccess.Rules;

namespace XUnitTest.DataAccess.Rules;

/// <summary>
/// One table of the new rule messages and the shapes of a date relative to now, which the client's copy
/// (<c>utils/variable-syntax.ts</c> and the validation rule utils) runs too, so the two copies can't drift.
/// Changing a message means changing <c>rule-message-cases.json</c>, and the client then fails until it follows.
/// </summary>
public class RuleMessageCasesTests
{
    private static readonly JsonElement Cases = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DataAccess", "Rules", "rule-message-cases.json"))).RootElement;

    [Fact]
    public void EveryFixedMessage_IsInTheTable_WordForWord()
    {
        var inCode = typeof(ValidationRuleCheck)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .ToDictionary(field => field.Name, field => (string)field.GetRawConstantValue()!);
        var inTable = Cases.GetProperty("messages").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);

        Assert.Equal(inCode.OrderBy(p => p.Key), inTable.OrderBy(p => p.Key));
    }

    [Fact]
    public void EveryFormattedMessage_ReadsAsInTheTable()
    {
        foreach (var row in Cases.GetProperty("formatted").EnumerateArray())
        {
            var name = row.GetProperty("name").GetString()!;
            var args = row.GetProperty("args").EnumerateArray().Select(a => (object?)a.GetString()).ToArray();
            var method = typeof(ValidationRuleCheck).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;
            Assert.Equal(row.GetProperty("text").GetString(), (string)method.Invoke(null, args)!);
        }
    }

    [Fact]
    public void DateRelativeToNow_ReadsEveryShapeAsTheTableSays()
    {
        var now = Cases.GetProperty("relativeNow");
        foreach (var text in now.GetProperty("valid").EnumerateArray().Select(v => v.GetString()))
            Assert.True(RelativeNow.IsWritten(text) && RelativeNow.TryParse(text, out _), text);
        foreach (var text in now.GetProperty("invalid").EnumerateArray().Select(v => v.GetString()))
            Assert.True(RelativeNow.IsWritten(text) && !RelativeNow.TryParse(text, out _), text);
        foreach (var text in now.GetProperty("notThisForm").EnumerateArray().Select(v => v.GetString()))
            Assert.False(RelativeNow.IsWritten(text), text);
    }
}
