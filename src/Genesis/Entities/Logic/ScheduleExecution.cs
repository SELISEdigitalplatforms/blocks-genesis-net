using Blocks.Genesis;
using Blocks.Genesis.Entities.Iam;
using MongoDB.Bson.Serialization.Attributes;

namespace Blocks.Genesis.Entities.Logic;

/// <summary>
/// One fire of a schedule, written by Scheduler.Worker to the per-tenant <c>ScheduleExecutions</c> collection
/// after the fire completes, and kept for all time. Only <c>ItemId</c> and <c>ScheduleId</c> are guaranteed;
/// every other field is nullable.
/// <para>
/// Outcome (<c>Response["Status"]</c>), duration (<see cref="FinishedUtc"/> minus <see cref="FiredUtc"/>) and the
/// trigger details (type, method, URL, headers, payload, cron, time zone, window, all from <see cref="Snapshot"/>)
/// are derived by the consumer; nothing else is stored. Machine-written, so not open to admin rules
/// (no <c>[SystemSchemaRules]</c>).
/// </para>
/// </summary>
[BsonIgnoreExtraElements]
public sealed class ScheduleExecution : BaseEntity
{
    /// <summary>Key of the HTTP status code in <see cref="Response"/>.</summary>
    public const string ResponseStatusKey = "Status";

    /// <summary>Key of the response body (or error summary) in <see cref="Response"/>.</summary>
    public const string ResponseBodyKey = "Body";

    /// <summary><see cref="BaseEntity.ItemId"/> of the <see cref="Schedule"/> that fired.</summary>
    public required string ScheduleId { get; set; }

    /// <summary>Owning tenant id.</summary>
    public string? TenantId { get; set; }

    /// <summary>Snapshot of the schedule name at fire time.</summary>
    public string? ScheduleName { get; set; }

    /// <summary>When delivery started (UTC).</summary>
    public DateTime? FiredUtc { get; set; }

    /// <summary>When delivery ended (UTC).</summary>
    public DateTime? FinishedUtc { get; set; }

    /// <summary>
    /// Outcome of the fire, as a key/value bag stored as an embedded BSON object (so the consumer can index,
    /// filter and count on <c>Response.Status</c>). <c>null</c> is not written and reads back as <c>null</c>.
    /// The keys are a documented contract enforced by the consumer; this library does not validate them, and
    /// unknown keys are kept in the bag on read. Query it by element path (for example
    /// <c>Builders&lt;ScheduleExecution&gt;.Filter.Eq("Response.Status", 504)</c>); typed LINQ over the dictionary
    /// may not translate with this serializer.
    /// <list type="table">
    /// <listheader><term>Key</term><description>CLR value / BSON: meaning</description></listheader>
    /// <item>
    /// <term><c>Status</c> (<see cref="ResponseStatusKey"/>)</term>
    /// <description>
    /// <c>int</c> / Int32. Always a real HTTP status code (100-599), never <c>0</c>. It is the target's code when the
    /// target answered, <c>202</c> for an accepted queue send, and otherwise a code the consumer picks to describe
    /// the failure (e.g. 502, 504, 422). Success = 200-299 (consumer rule).
    /// </description>
    /// </item>
    /// <item>
    /// <term><c>Body</c> (<see cref="ResponseBodyKey"/>)</term>
    /// <description>
    /// <c>string</c> / string. Response body (at most 16 KB, truncated by the consumer), or the consumer's error
    /// summary when the target didn't answer. The key is omitted when there is no body (a null entry would be
    /// dropped on read anyway).
    /// </description>
    /// </item>
    /// </list>
    /// </summary>
    [BsonIgnoreIfNull]
    [BsonSerializer(typeof(AttributeBagSerializer))]
    public Dictionary<string, object>? Response { get; set; }

    /// <summary>
    /// The <see cref="Schedule"/> at fire time, serialized as JSON by the consumer, with secrets redacted
    /// (consumer rule). A string, not a nested document, so a future <see cref="Schedule"/> shape change never
    /// breaks reading old rows.
    /// </summary>
    public string? Snapshot { get; set; }
}
