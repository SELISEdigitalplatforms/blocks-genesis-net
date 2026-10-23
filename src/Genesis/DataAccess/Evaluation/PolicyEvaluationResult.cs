using MongoDB.Bson;

namespace Blocks.Genesis.DataAccess.Evaluation;

/// <summary>Stable error codes for data-access decisions, for hosts to return to clients.</summary>
public static class PolicyErrorCodes
{
    /// <summary>A policy denied the caller (HTTP 403). Not an authentication failure.</summary>
    public const string Forbidden = "FORBIDDEN";

    /// <summary>The message for a denial that names no field.</summary>
    public const string NoPolicyGrantsAccess = "No policy grants access to this resource.";
}

/// <summary>
/// Result of evaluating a policy rule group.
/// </summary>
public class PolicyEvaluationResult
{
    /// <summary>
    /// Whether access is granted based on token validation.
    /// </summary>
    public bool IsAccessGranted { get; set; }

    /// <summary>
    /// MongoDB filter to apply for data-level filtering.
    /// Empty document means no additional filtering needed.
    /// </summary>
    public BsonDocument DataFilter { get; set; } = new();

    /// <summary>
    /// Fields that should be excluded from the result (for CLS). On a refused write, the fields the caller may not write.
    /// </summary>
    public List<string> ExcludedFields { get; set; } = [];

    /// <summary>
    /// Error message if evaluation failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// <see cref="PolicyErrorCodes.Forbidden"/> when a policy decision denied the caller; null when granted.
    /// </summary>
    public string? ErrorCode { get; set; }

    /// <summary>
    /// Indicates if the policy requires a database filter vs pure token validation.
    /// </summary>
    public bool RequiresDataFilter { get; set; }

    /// <summary>A denial with code <see cref="PolicyErrorCodes.Forbidden"/>.</summary>
    public static PolicyEvaluationResult Forbidden(string message, IEnumerable<string>? fields = null) => new()
    {
        IsAccessGranted = false,
        ErrorMessage = message,
        ErrorCode = PolicyErrorCodes.Forbidden,
        ExcludedFields = fields?.ToList() ?? []
    };

    /// <summary>Throws <see cref="BlocksForbiddenException"/> when access is not granted.</summary>
    public void ThrowIfDenied()
    {
        if (!IsAccessGranted)
            throw new BlocksForbiddenException(ErrorMessage ?? PolicyErrorCodes.NoPolicyGrantsAccess, ErrorCode ?? PolicyErrorCodes.Forbidden);
    }
}
