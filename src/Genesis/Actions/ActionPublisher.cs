using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Blocks.Genesis;

/// <summary>
/// Sends one <see cref="BlocksAction"/> per target queue the current tenant turned on for an action. Best-effort:
/// it logs and swallows every failure, never retries, and never logs or tags the data.
/// </summary>
internal sealed class ActionPublisher : IActionPublisher
{
    internal const string ActivityName = "action.publish";
    internal const string NameTag = "action.name";
    internal const string EventIdTag = "action.event_id";
    internal const string OutcomeTag = "action.outcome";
    internal const string SkipReasonTag = "action.skip_reason";

    internal const string OutcomeSent = "sent";
    internal const string OutcomePartial = "partial";
    internal const string OutcomeFailed = "failed";
    internal const string OutcomeSkipped = "skipped";

    internal const string SkipNoName = "no_name";
    internal const string SkipNoTenant = "no_tenant";
    internal const string SkipReadFailed = "read_failed";
    internal const string SkipNotFound = "not_found";
    internal const string SkipNoTarget = "no_target";
    internal const string SkipTooLarge = "too_large";
    internal const string SkipNoBus = "no_bus";

    internal const string EventIdPrefix = "evt_";
    internal const string EmptyData = "{}";

    /// <summary>
    /// Longest the publish waits for the tenant's action list. The caller awaits it after its own save, so a slow
    /// or unreachable tenant DB must not hold the caller for the driver's own timeout.
    /// </summary>
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    // camelCase, so a C# type's UserId matches the payload key userId; dictionary keys are kept as they are.
    private static readonly JsonSerializerOptions DataJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IKeyValueStore _keyValueStore;
    private readonly IMessageClient? _messageClient;
    private readonly ActivitySource _activitySource;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ActionPublisher> _logger;

    public ActionPublisher(
        IKeyValueStore keyValueStore,
        IMessageClient? messageClient,
        ActivitySource activitySource,
        TimeProvider timeProvider,
        ILogger<ActionPublisher> logger)
    {
        _keyValueStore = keyValueStore;
        _messageClient = messageClient;
        _activitySource = activitySource;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task PublishAsync(string action, object? data, string? organizationId = null, CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity(ActivityName);
        activity?.SetTag(NameTag, action);

        try
        {
            await PublishCoreAsync(action, data, organizationId, activity, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Type only: a serializer or transport error can quote the data.
            _logger.LogWarning("Action {Action} not published ({ErrorType})", action, ex.GetType().Name); // NOSONAR S6667 — type only
            activity?.SetTag(OutcomeTag, OutcomeFailed);
        }
    }

    private async Task PublishCoreAsync(string action, object? data, string? organizationId, Activity? activity, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            _logger.LogWarning("Action without a name not published");
            Skip(activity, SkipNoName);
            return;
        }

        if (_messageClient is null)
        {
            // A host without a message bus (no Azure or RabbitMQ config) cannot publish; it must not fail.
            _logger.LogWarning("Action {Action} not published: no message bus in this host", action);
            Skip(activity, SkipNoBus);
            return;
        }

        var context = BlocksContext.GetContext();
        var tenantId = context?.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            _logger.LogWarning("Action {Action} not published: no tenant", action);
            Skip(activity, SkipNoTenant);
            return;
        }

        var definition = await FindDefinitionAsync(action, tenantId, activity, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            return;
        }

        var queues = TargetQueues(definition);
        if (queues.Count == 0)
        {
            Skip(activity, SkipNoTarget);
            return;
        }

        var message = new BlocksAction
        {
            Id = EventIdPrefix + Guid.NewGuid().ToString("N"),
            Action = action,
            Time = _timeProvider.GetUtcNow().UtcDateTime,
            OrganizationId = ResolveOrganization(context, organizationId),
            DataJson = FilterData(data, definition.Payload)
        };
        activity?.SetTag(EventIdTag, message.Id);

        var size = EnvelopeBytes(message);
        if (size > ActionConstants.MaxMessageBytes)
        {
            _logger.LogWarning(
                "Action {Action} ({EventId}) for tenant {TenantId} dropped: size={Size} bytes exceeds {MaxBytes}",
                action, message.Id, tenantId, size, ActionConstants.MaxMessageBytes);
            Skip(activity, SkipTooLarge);
            return;
        }

        var failed = await SendAllAsync(_messageClient, message, queues).ConfigureAwait(false);
        activity?.SetTag(OutcomeTag, Outcome(failed, queues.Count));
    }

    // Null when the action is not published; the reason is already logged and tagged.
    private async Task<ActionDefinition?> FindDefinitionAsync(string action, string tenantId, Activity? activity, CancellationToken cancellationToken)
    {
        List<ActionDefinition>? definitions;
        try
        {
            definitions = await ReadActionsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning( // NOSONAR S6667 — type only: the exception can quote stored values
                "Action {Action} not published: action list read failed for tenant {TenantId} ({ErrorType})",
                action, tenantId, ex.GetType().Name);
            Skip(activity, SkipReadFailed);
            return null;
        }

        var definition = definitions?.Find(d => d is not null && string.Equals(d.Name, action, StringComparison.Ordinal));
        if (definition is null)
        {
            _logger.LogInformation("Action {Action} is not in the action list of tenant {TenantId}", action, tenantId);
            Skip(activity, SkipNotFound);
        }

        return definition;
    }

    // Starts the read synchronously on the caller's flow, so the impersonated read resolves the caller's tenant.
    private async Task<List<ActionDefinition>?> ReadActionsAsync(CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var read = _keyValueStore.GetAsync<List<ActionDefinition>>(ActionConstants.ActionsKey, impersonated: true, cancellation.Token);

        try
        {
            // WaitAsync bounds the wait even for a store that ignores the token.
            return await read.WaitAsync(ReadTimeout, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            // The abandoned read may still fault; observe it so it is not reported as unobserved.
            _ = read.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    private static List<string> TargetQueues(ActionDefinition definition)
    {
        var queues = new List<string>(3);
        if (definition.Workflow)
        {
            queues.Add(ActionConstants.WorkflowQueue);
        }

        if (definition.Function)
        {
            queues.Add(ActionConstants.FunctionQueue);
        }

        if (definition.Webhook)
        {
            queues.Add(ActionConstants.WebhookQueue);
        }

        return queues;
    }

    // An explicit value wins (empty means none); otherwise the context's organization, empty meaning none.
    private static string? ResolveOrganization(BlocksContext? context, string? organizationId)
    {
        var value = organizationId ?? context?.OrganizationId;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// The top-level fields of <paramref name="data"/> (camelCase names) that are keys of
    /// <paramref name="payload"/>, as a JSON object. Anything else, and data that is not an object, is dropped.
    /// </summary>
    internal static string FilterData(object? data, IReadOnlyDictionary<string, string>? payload)
    {
        if (data is null || payload is null || payload.Count == 0)
        {
            return EmptyData;
        }

        using var document = JsonSerializer.SerializeToDocument(data, data.GetType(), DataJsonOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return EmptyData;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            // A JSON object may repeat a name; the first one wins.
            var kept = document.RootElement.EnumerateObject()
                .Where(p => payload.ContainsKey(p.Name))
                .DistinctBy(p => p.Name, StringComparer.Ordinal);
            foreach (var property in kept)
            {
                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // The same envelope the message clients put on the wire.
    private static int EnvelopeBytes(BlocksAction message) =>
        Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new Message
        {
            Body = JsonSerializer.Serialize(message),
            Type = nameof(BlocksAction)
        }));

    private async Task<int> SendAllAsync(IMessageClient messageClient, BlocksAction message, List<string> queues)
    {
        var failed = 0;
        foreach (var queue in queues)
        {
            try
            {
                await messageClient.SendToConsumerAsync(new ConsumerMessage<BlocksAction>
                {
                    ConsumerName = queue,
                    Payload = message
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning( // NOSONAR S6667 — type only: the exception can quote message content
                    "Action {Action} ({EventId}) not sent to {Queue} ({ErrorType})",
                    message.Action, message.Id, queue, ex.GetType().Name);
            }
        }

        return failed;
    }

    private static string Outcome(int failed, int total)
    {
        if (failed == 0)
        {
            return OutcomeSent;
        }

        return failed == total ? OutcomeFailed : OutcomePartial;
    }

    private static void Skip(Activity? activity, string reason)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(OutcomeTag, OutcomeSkipped);
        activity.SetTag(SkipReasonTag, reason);
    }
}
