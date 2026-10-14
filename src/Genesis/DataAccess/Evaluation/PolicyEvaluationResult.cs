using MongoDB.Bson;

namespace Blocks.Genesis.DataAccess.Evaluation;

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
    /// Fields that should be excluded from the result (for CLS).
    /// </summary>
    public List<string> ExcludedFields { get; set; } = [];

    /// <summary>
    /// Error message if evaluation failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Indicates if the policy requires a database filter vs pure token validation.
    /// </summary>
    public bool RequiresDataFilter { get; set; }
}
