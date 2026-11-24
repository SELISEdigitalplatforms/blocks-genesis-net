using System.Globalization;

namespace Blocks.Genesis;

/// <summary>
/// An access rule could not be checked, so the request is refused instead of guessing: an EXISTS_IN / NOT_EXISTS_IN
/// rule reached evaluation without its lookup resolved, or a rule has an operator the evaluators don't know.
/// <para>
/// <see cref="Exception.Message"/> is caller-facing and names the schema only, never a field or a rule set.
/// <see cref="Reason"/> is for logs only.
/// </para>
/// <para>
/// The evaluators throw it with an empty <see cref="SchemaName"/> because they don't know the schema. Callers that do
/// know it catch the exception and throw a new one with the schema name, passing the original as the inner exception;
/// <see cref="SchemaName"/> is set through the constructor only.
/// </para>
/// </summary>
public sealed class AccessCheckFailedException(string schemaName, string reason, Exception? innerException = null)
    : Exception(BuildMessage(schemaName), innerException)
{
    /// <summary>The schema whose access couldn't be checked; empty where the thrower doesn't know it.</summary>
    public string SchemaName { get; } = schemaName ?? string.Empty;

    /// <summary>Why the check failed, for logs. Never shown to the caller.</summary>
    public string Reason { get; } = reason ?? string.Empty;

    private static string BuildMessage(string? schemaName) => string.Format(
        CultureInfo.InvariantCulture,
        "Access to {0} couldn't be checked. Ask the project's admin to review its access rules.",
        schemaName ?? string.Empty);
}
