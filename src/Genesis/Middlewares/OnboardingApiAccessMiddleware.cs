using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Collections.Frozen;
using System.Globalization;

namespace Blocks.Genesis;

/// <summary>
/// Limits which APIs a root-tenant token may call on another tenant to the root <c>AllowedApis</c> list.
/// The list is read only for root cross-tenant requests, cached per instance, and a failed load never
/// throws to the request: it serves the last good list, or answers 503 when there is none.
/// </summary>
public class OnboardingApiAccessMiddleware
{
    /// <summary>How long a successful load is trusted, counted from the load's start. Includes an empty list.</summary>
    internal static readonly TimeSpan AllowedApisCacheLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Longest a request waits for the root read. A timed-out read is a failed load.</summary>
    internal static readonly TimeSpan AllowedApisLoadTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How long a failed load is cached before the next read, counted from when the failure is observed.</summary>
    internal static readonly TimeSpan AllowedApisFailureRetryDelay = TimeSpan.FromSeconds(30);

    private const string UnavailableMessage = "Service_Unavailable: Os_Allowed_Apis_Unavailable";

    private static readonly int _retrySeconds = (int)AllowedApisFailureRetryDelay.TotalSeconds;
    private static readonly string _retryAfter = _retrySeconds.ToString(CultureInfo.InvariantCulture);

    private readonly RequestDelegate _next;
    private readonly ITenants _tenants;
    private readonly ILogger<OnboardingApiAccessMiddleware> _logger;
    private readonly IOsAllowedApiSource _source;
    private readonly TimeProvider _timeProvider;

    // Published only by the load task, before the in-flight slot is cleared.
    private CachedAllowedApis? _entry;
    private Lazy<Task<CachedAllowedApis>>? _inFlight;

    public OnboardingApiAccessMiddleware(
        RequestDelegate next,
        ITenants tenants,
        IBlocksSecret blocksSecret,
        ILogger<OnboardingApiAccessMiddleware> logger,
        string[] pathPrefixes)
        // Arguments run left to right: the existing next/tenants/logger checks, then blocksSecret, then the one client.
        : this(
            next ?? throw new ArgumentNullException(nameof(next)),
            tenants ?? throw new ArgumentNullException(nameof(tenants)),
            logger ?? throw new ArgumentNullException(nameof(logger)),
            CreateRootSource(blocksSecret),
            TimeProvider.System)
    {
    }

    /// <summary>Test seam: a custom allow-list source and clock.</summary>
    internal OnboardingApiAccessMiddleware(
        RequestDelegate next,
        ITenants tenants,
        ILogger<OnboardingApiAccessMiddleware> logger,
        IOsAllowedApiSource source,
        TimeProvider timeProvider)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        BlocksHttpContextAccessor.EnsureInitialized(context);

        var endpoint = context.GetEndpoint();
        if (endpoint is null || (endpoint.DisplayName?.Contains("Controller") == false && endpoint.DisplayName?.Contains("GraphQL") == false))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var blocksContext = BlocksContext.GetContext();
        if (blocksContext is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var tenantId = blocksContext.TenantId;

        // Only a root token acting on another tenant can be blocked; nothing else needs the allow-list.
        if (blocksContext.OriginalTenantId == tenantId || !IsRootTenant(tenantId))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var allowedApis = await GetAllowedApisAsync().ConfigureAwait(false);
        if (allowedApis is null)
        {
            // Fail closed: no list has loaded, so nothing is known to be allowed.
            context.Response.Headers.RetryAfter = _retryAfter;
            await TenantContextHelper.RejectRequest(context, StatusCodes.Status503ServiceUnavailable, UnavailableMessage).ConfigureAwait(false);
            return;
        }

        if (!IsOsAllowedApi(context.Request.Path, allowedApis))
        {
            _logger.LogWarning("Blocked cross-tenant onboarding API access to {Path} for tenant {TenantId}.", context.Request.Path, tenantId);
            await TenantContextHelper.RejectRequest(context, StatusCodes.Status403Forbidden, "Forbidden: Cross_Tenant_Access_Not_Allowed").ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private static MongoOsAllowedApiSource CreateRootSource(IBlocksSecret blocksSecret)
    {
        ArgumentNullException.ThrowIfNull(blocksSecret);
        return new MongoOsAllowedApiSource(
            new MongoClient(blocksSecret.DatabaseConnectionString).GetDatabase(blocksSecret.RootDatabaseName));
    }

    private bool IsRootTenant(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return false;
        }

        var tenant = _tenants.GetTenantByID(tenantId);
        return tenant is { IsRootTenant: true };
    }

    private static bool IsOsAllowedApi(PathString requestPath, FrozenSet<string> allowedApis)
    {
        var normalized = NormalizePath(requestPath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return allowedApis.Contains(normalized);
    }

    private static string NormalizePath(PathString requestPath)
    {
        var value = requestPath.Value?.Trim('/') ?? string.Empty;
        return value;
    }

    /// <summary>The cached list, or <c>null</c> when it is unavailable. Completes synchronously on a fresh entry.</summary>
    private ValueTask<FrozenSet<string>?> GetAllowedApisAsync()
    {
        var entry = Volatile.Read(ref _entry);
        if (entry is not null && entry.ExpiresAt > _timeProvider.GetUtcNow())
        {
            return new ValueTask<FrozenSet<string>?>(entry.Apis);
        }

        return new ValueTask<FrozenSet<string>?>(LoadSingleFlightAsync());
    }

    private async Task<FrozenSet<string>?> LoadSingleFlightAsync()
    {
        var candidate = new Lazy<Task<CachedAllowedApis>>(LoadAsync);
        var load = Interlocked.CompareExchange(ref _inFlight, candidate, null) ?? candidate;
        try
        {
            return (await load.Value.ConfigureAwait(false)).Apis;
        }
        finally
        {
            // The load stored its entry before completing, so callers arriving now see it fresh.
            Interlocked.CompareExchange(ref _inFlight, null, load);
        }
    }

    private async Task<CachedAllowedApis> LoadAsync()
    {
        var previous = Volatile.Read(ref _entry);
        var startedAt = _timeProvider.GetUtcNow();

        // A caller that saw a stale entry may take the slot just after another load stored a fresh one.
        if (previous is not null && previous.ExpiresAt > startedAt)
        {
            return previous;
        }

        CachedAllowedApis entry;
        try
        {
            var document = await ReadWithTimeoutAsync().ConfigureAwait(false);
            // Lifetimes count from the read's start, so a slow read never extends how long a result is trusted.
            entry = new CachedAllowedApis(ParseAllowedApis(document), startedAt + AllowedApisCacheLifetime);
        }
        catch (Exception ex)
        {
            var retryAt = _timeProvider.GetUtcNow() + AllowedApisFailureRetryDelay;
            if (previous?.Apis is { } lastGood)
            {
                OnboardingApiAccessLog.ServingLastGoodList(_logger, ex.GetType().Name, _retrySeconds); // NOSONAR S6667 — type only: the exception can quote connection details
                entry = new CachedAllowedApis(lastGood, retryAt);
            }
            else
            {
                OnboardingApiAccessLog.ListUnavailable(_logger, ex.GetType().Name, _retrySeconds); // NOSONAR S6667 — type only: the exception can quote connection details
                entry = new CachedAllowedApis(null, retryAt);
            }
        }

        Volatile.Write(ref _entry, entry);
        return entry;
    }

    private async Task<BsonDocument?> ReadWithTimeoutAsync()
    {
        // Owned by the load, not linked to any request, so one caller leaving does not cancel the shared read.
        using var cancellation = new CancellationTokenSource();
        var read = _source.ReadAsync(cancellation.Token);

        try
        {
            // WaitAsync bounds the wait even for a source that ignores the token.
            return await read.WaitAsync(AllowedApisLoadTimeout, _timeProvider).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            // The abandoned read may still fault; observe it so it is not reported as unobserved. Its result is discarded.
            _ = read.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }

    // Keeps non-blank string entries; other elements are skipped. No document, no field or a non-array is an empty list.
    private static FrozenSet<string> ParseAllowedApis(BsonDocument? document)
    {
        if (document is null || !document.TryGetValue("AllowedApis", out var allowedApis) || !allowedApis.IsBsonArray)
        {
            return FrozenSet<string>.Empty;
        }

        return allowedApis.AsBsonArray
            .Where(x => x.IsString && !string.IsNullOrWhiteSpace(x.AsString))
            .Select(x => x.AsString)
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary><see cref="Apis"/> is <c>null</c> when the list is unavailable.</summary>
    private sealed record CachedAllowedApis(FrozenSet<string>? Apis, DateTimeOffset ExpiresAt);
}

internal static partial class OnboardingApiAccessLog
{
    [LoggerMessage(EventId = 8001, Level = LogLevel.Warning, Message = "Onboarding allowed-API list could not be loaded ({ErrorType}); serving the last good list for {RetrySeconds} s.")]
    public static partial void ServingLastGoodList(ILogger logger, string errorType, int retrySeconds);

    [LoggerMessage(EventId = 8002, Level = LogLevel.Warning, Message = "Onboarding allowed-API list could not be loaded ({ErrorType}); root cross-tenant requests get 503 for {RetrySeconds} s.")]
    public static partial void ListUnavailable(ILogger logger, string errorType, int retrySeconds);
}
