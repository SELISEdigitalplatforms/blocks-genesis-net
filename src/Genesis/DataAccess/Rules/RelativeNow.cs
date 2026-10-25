using System.Globalization;
using System.Text.RegularExpressions;

namespace Blocks.Genesis.DataAccess.Rules;

/// <summary>
/// A date relative to the moment a rule is checked, written as a comparison value on a DateTime property:
/// <c>{{Now}}</c>, <c>{{Now-18y}}</c>, <c>{{Now+30d}}</c>. Units: y, mo, d, h, min; the amount is 1–9999.
/// Only rules at evaluation version 1 or later read it; in an older rule the text stays text.
/// </summary>
public readonly record struct RelativeNow(int Amount, string Unit)
{
    public const string Shapes = "{{Now}}, {{Now-18y}} or {{Now+30d}} (units y, mo, d, h, min)";

    // "{{Now}}", or "{{Now" followed by a sign: anything else starting "{{Now" ("{{Nowhere}}", "{{Now.X}}") is not this form.
    private static readonly Regex Start = new(@"^\s*\{\{\s*Now\s*(\}\}|[+-])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Shape = new(
        @"^\s*\{\{\s*Now\s*(?:(?<sign>[+-])\s*(?<amount>[1-9][0-9]{0,3})\s*(?<unit>y|mo|d|h|min))?\s*\}\}\s*$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>True when the value is meant as a date relative to now, well formed or not.</summary>
    public static bool IsWritten(object? value) => value is string text && Start.IsMatch(text);

    /// <summary>Reads a well-formed value; false for anything else.</summary>
    public static bool TryParse(object? value, out RelativeNow now)
    {
        now = default;
        if (value is not string text || Shape.Match(text) is not { Success: true } match)
            return false;
        if (!match.Groups["sign"].Success)
            return true;

        var amount = int.Parse(match.Groups["amount"].Value, CultureInfo.InvariantCulture);
        now = new RelativeNow(match.Groups["sign"].Value == "-" ? -amount : amount, match.Groups["unit"].Value);
        return true;
    }

    /// <summary>
    /// The moment this means, from <paramref name="nowUtc"/>. Years and months use calendar maths, so "18 years ago" is
    /// exact. Years, months and days mean whole UTC days: the start of that day, which is the date messages name.
    /// An offset past the calendar's ends is clamped to them.
    /// </summary>
    public DateTime From(DateTime nowUtc)
    {
        try
        {
            var moment = Unit switch
            {
                "y" => nowUtc.AddYears(Amount),
                "mo" => nowUtc.AddMonths(Amount),
                "d" => nowUtc.AddDays(Amount),
                "h" => nowUtc.AddHours(Amount),
                "min" => nowUtc.AddMinutes(Amount),
                _ => nowUtc,
            };
            return ShowsTime ? moment : DateTime.SpecifyKind(moment.Date, DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Amount < 0 ? DateTime.MinValue : DateTime.MaxValue;
        }
    }

    /// <summary>True when the time of day matters to the reader: plain now, hours and minutes.</summary>
    public bool ShowsTime => Unit is null or "" or "h" or "min";

    /// <summary>How a message names it: "now", "now − 18 years", "now + 1 month".</summary>
    public string Describe()
    {
        if (string.IsNullOrEmpty(Unit))
            return "now";
        var count = Math.Abs(Amount);
        var unit = Unit switch { "y" => "year", "mo" => "month", "d" => "day", "h" => "hour", _ => "minute" };
        return $"now {(Amount < 0 ? "−" : "+")} {count} {unit}{(count == 1 ? "" : "s")}";
    }
}
