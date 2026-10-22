using Blocks.Genesis.DataAccess.Evaluation;
using Blocks.Genesis.Entities.Data;
using MongoDB.Bson;

namespace Blocks.Genesis.DataAccess;

/// <summary>
/// Resolves a filter from a schema name alone: loads the schema definition, its access policies and
/// its validations from the caller's tenant database, builds the <see cref="SchemaAccessRequest"/> for
/// the current principal, and hands it to <see cref="IDataAccessPolicyResolutionService"/>.
/// </summary>
public interface IDataAccessResolver
{
    /// <summary>Load, then <see cref="IDataAccessPolicyResolutionService.Check"/> and <see cref="IDataAccessPolicyResolutionService.BuildReadFilter"/>. Throws <see cref="KeyNotFoundException"/> when the schema does not exist.</summary>
    Task<ResolvedFilter> ResolveReadFilterAsync(string schemaName, BsonDocument userFilter, CancellationToken cancellationToken = default);

    /// <summary>Load, then <see cref="IDataAccessPolicyResolutionService.Check"/> and <see cref="IDataAccessPolicyResolutionService.ApplyMutationFilter"/> for WRITE, EDIT or DELETE. <see cref="ResolvedFilter.Filter"/> is null when denied.</summary>
    Task<ResolvedFilter> ResolveMutationFilterAsync(string schemaName, PolicyOperation operation, BsonDocument baseFilter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Everything a query needs: READ gate, final filter, projection for <paramref name="requestedFields"/>
    /// (null = every field), and the column read policy. The caller runs the find and calls <see cref="ResolvedRead.ApplyReadPolicy"/> per row.
    /// </summary>
    Task<ResolvedRead> ResolveReadAsync(string schemaName, BsonDocument userFilter, IReadOnlyList<string>? requestedFields, CancellationToken cancellationToken = default);

    /// <summary>READ gate plus the projection and the column read policy alone, for callers that build the filter themselves.</summary>
    Task<ResolvedProjection> ResolveProjectionAsync(string schemaName, IReadOnlyList<string>? requestedFields, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gate for <paramref name="operation"/> (WRITE or EDIT) plus the column rules on <paramref name="input"/>. Root fields
    /// the schema does not define are removed in place (<see cref="ResolvedInput.RemovedFields"/>). A field a column rule
    /// refuses denies the whole input (code <c>FORBIDDEN</c>, the field named in the message) instead of being removed.
    /// On WRITE a non-empty input must also match the WRITE row filter; an empty input is the gate alone.
    /// </summary>
    Task<ResolvedInput> ResolveInputAsync(string schemaName, PolicyOperation operation, IDictionary<string, object?> input, CancellationToken cancellationToken = default);

    /// <summary><see cref="ResolveInputAsync"/> for WRITE.</summary>
    Task<ResolvedInput> ResolveInsertAsync(string schemaName, IDictionary<string, object?> input, CancellationToken cancellationToken = default);

    /// <summary>
    /// EDIT gate, final filter from <paramref name="baseFilter"/> (only rows inside the EDIT row filter are updated), and
    /// the column rules on <paramref name="input"/> (refused, not removed, as in <see cref="ResolveInputAsync"/>). Without the
    /// stored row this cannot check the resulting row or column rules that read a field: use <see cref="CheckUpdateAsync"/>
    /// or <see cref="CheckReplaceAsync"/> for that.
    /// </summary>
    Task<ResolvedUpdate> ResolveUpdateAsync(string schemaName, BsonDocument baseFilter, IDictionary<string, object?> input, CancellationToken cancellationToken = default);

    /// <summary>
    /// The policy check for inserting <paramref name="row"/>: WRITE gate, column rules on every field (a rule that reads a
    /// field is checked against the new row), and the new row must match the WRITE row filter.
    /// </summary>
    Task<ResolvedRowCheck> CheckInsertAsync(string schemaName, BsonDocument row, CancellationToken cancellationToken = default);

    /// <summary>
    /// The policy check for replacing <paramref name="storedRow"/> with <paramref name="newRow"/>: EDIT gate, the stored row
    /// and the new row must both match the EDIT row filter, and the column rules on every changed field (a rule that reads a
    /// field is checked against the stored row).
    /// </summary>
    Task<ResolvedRowCheck> CheckReplaceAsync(string schemaName, BsonDocument storedRow, BsonDocument newRow, CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="CheckReplaceAsync"/> for a MongoDB update document (<c>$set</c>, <c>$unset</c>, <c>$inc</c>, <c>$currentDate</c>
    /// are applied in memory; a replacement document is a replace). An operator that is not applied in memory is refused when
    /// it touches a field the EDIT row filter reads.
    /// </summary>
    Task<ResolvedRowCheck> CheckUpdateAsync(string schemaName, BsonDocument storedRow, BsonDocument update, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validation rules and default values for a write. On insert (<paramref name="isInsert"/>), defaults
    /// fill properties that are missing or null in <paramref name="document"/>; then the schema's active
    /// rules are checked, Reference rules against the same database. For an update, pass only the changed
    /// fields: only those are checked. <paramref name="storedRecord"/> is the record being updated, which
    /// a rule's variable (e.g. <c>{{Schema.StartDate}}</c>) reads when the property isn't in the document.
    /// System schemas only get what the <see cref="Rules.SystemSchemaRules"/> allow-list opens; with
    /// nothing configured the document comes back unchanged. Throws <see cref="KeyNotFoundException"/>
    /// when the schema does not exist.
    /// </summary>
    Task<ResolvedWrite> ResolveWriteAsync(string schemaName, BsonDocument document, bool isInsert, BsonDocument? storedRecord = null, CancellationToken cancellationToken = default);

    /// <summary>DELETE gate and final filter from <paramref name="baseFilter"/>.</summary>
    Task<ResolvedFilter> ResolveDeleteAsync(string schemaName, BsonDocument baseFilter, CancellationToken cancellationToken = default);
}
