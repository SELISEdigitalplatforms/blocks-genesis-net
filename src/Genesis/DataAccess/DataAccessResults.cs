using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;

namespace Blocks.Genesis.DataAccess;

/// <summary>
/// A read resolved from a schema name: the final filter, the projection to request, and <see cref="Mask"/>
/// to apply to every returned row. <see cref="Filter"/> and <see cref="Projection"/> are null when denied.
/// </summary>
public sealed class ResolvedRead
{
    private readonly Func<Dictionary<string, object>, Dictionary<string, object>>? _mask;

    internal ResolvedRead(PolicyEvaluationResult evaluation, BsonDocument? filter, BsonDocument? projection,
        Func<Dictionary<string, object>, Dictionary<string, object>>? mask)
    {
        Evaluation = evaluation;
        Filter = filter;
        Projection = projection;
        _mask = mask;
    }

    public PolicyEvaluationResult Evaluation { get; }
    public bool IsAccessGranted => Evaluation.IsAccessGranted;
    public BsonDocument? Filter { get; }
    public BsonDocument? Projection { get; }

    /// <summary>Applies column-level masking to one row (modified in place and returned). Throws when access was denied.</summary>
    public Dictionary<string, object> Mask(Dictionary<string, object> row) =>
        _mask is null ? throw new InvalidOperationException("Access was denied; there is nothing to mask.") : _mask(row);
}

/// <summary>A projection resolved on its own: same as <see cref="ResolvedRead"/> without the filter.</summary>
public sealed class ResolvedProjection
{
    private readonly Func<Dictionary<string, object>, Dictionary<string, object>>? _mask;

    internal ResolvedProjection(PolicyEvaluationResult evaluation, BsonDocument? projection,
        Func<Dictionary<string, object>, Dictionary<string, object>>? mask)
    {
        Evaluation = evaluation;
        Projection = projection;
        _mask = mask;
    }

    public PolicyEvaluationResult Evaluation { get; }
    public bool IsAccessGranted => Evaluation.IsAccessGranted;
    public BsonDocument? Projection { get; }

    /// <summary>Applies column-level masking to one row (modified in place and returned). Throws when access was denied.</summary>
    public Dictionary<string, object> Mask(Dictionary<string, object> row) =>
        _mask is null ? throw new InvalidOperationException("Access was denied; there is nothing to mask.") : _mask(row);
}

/// <summary>
/// An input resolved for WRITE or EDIT: the same dictionary the caller passed, with the fields the
/// caller may not write removed (<see cref="RemovedFields"/>). <see cref="Input"/> is null when denied.
/// </summary>
public sealed record ResolvedInput(
    PolicyEvaluationResult Evaluation,
    IDictionary<string, object?>? Input,
    IReadOnlyList<string> RemovedFields)
{
    public bool IsAccessGranted => Evaluation.IsAccessGranted;
}

/// <summary>An update resolved from a schema name: the final filter plus the restricted input. Both null when denied.</summary>
public sealed record ResolvedUpdate(
    PolicyEvaluationResult Evaluation,
    BsonDocument? Filter,
    IDictionary<string, object?>? Input,
    IReadOnlyList<string> RemovedFields)
{
    public bool IsAccessGranted => Evaluation.IsAccessGranted;
}

/// <summary>
/// Outcome of resolving a filter in one call: the RLS gate result and, when access is granted, the
/// final Mongo filter. <see cref="Filter"/> is <c>null</c> when <see cref="IsAccessGranted"/> is false.
/// </summary>
public sealed record ResolvedFilter(
    PolicyEvaluationResult Evaluation,
    BsonDocument? Filter)
{
    public bool IsAccessGranted => Evaluation.IsAccessGranted;
}
