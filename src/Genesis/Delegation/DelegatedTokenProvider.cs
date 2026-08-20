using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Blocks.Genesis;

/// <summary>
/// Exchanges a delegation grant for a short-lived Blocks access token, caching the result per
/// grant id so a job that runs for hours performs one exchange per token lifetime rather than
/// one per outbound call.
/// <para>
/// <b>Tenant comes from <see cref="BlocksContext"/>, never from a raw header.</b> In a worker it
/// was populated from the message <c>SecurityContext</c>; in an API request it comes from the
/// validated token.
/// </para>
/// <para>
/// The <see cref="Lazy{T}"/> wrapper is what gives single-flight: concurrent callers on one grant
/// observe the same in-flight exchange. The dictionary does not evict on its own, so entries are
/// removed when a message settles and swept opportunistically.
/// </para>
/// </summary>
public sealed class DelegatedTokenProvider : IDelegatedTokenProvider
{
    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt)
    {
        /// <summary>Servable until one minute before expiry.</summary>
        public bool IsUsable(DateTimeOffset now) => now < ExpiresAt - DelegationConstants.TokenRenewalMargin;
    }

    private const int SweepInterval = 64;

    private readonly ConcurrentDictionary<string, Lazy<Task<CachedToken?>>> _cache = new(StringComparer.Ordinal);
    private readonly ITenants _tenants;
    private readonly IDelegationTokenEndpointResolver _endpointResolver;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DelegatedTokenProvider> _logger;
    private readonly ActivitySource? _activitySource;
    private readonly TimeProvider _timeProvider;

    private int _callCount;

    public DelegatedTokenProvider(
        ITenants tenants,
        IDelegationTokenEndpointResolver endpointResolver,
        IHttpClientFactory httpClientFactory,
        ILogger<DelegatedTokenProvider> logger,
        ActivitySource? activitySource = null,
        TimeProvider? timeProvider = null)
    {
        _tenants = tenants;
        _endpointResolver = endpointResolver;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _activitySource = activitySource;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<string?> GetTokenAsync(CancellationToken ct = default)
    {
        var delegationId = DelegatedTokenContext.Current;
        if (string.IsNullOrWhiteSpace(delegationId)) return null;

        var tenantId = BlocksContext.GetContext()?.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            DelegatedTokenProviderLog.NoTenantInContext(_logger);
            return null;
        }

        if (Interlocked.Increment(ref _callCount) % SweepInterval == 0)
        {
            SweepExpired();
        }

        // At most one retry, and only to replace a cached entry that has aged out. An entry this
        // call created and then found unusable is a rejection from IAM: retrying it immediately
        // would double the load on the token endpoint for every failure.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var createdHere = false;

            var entry = _cache.GetOrAdd(
                delegationId!,
                _ =>
                {
                    createdHere = true;

                    // The exchange is shared work, so it deliberately does not take the first
                    // caller's token: one caller giving up must not cancel it for the other
                    // forty-nine. The HTTP client timeout is what bounds it.
                    return new Lazy<Task<CachedToken?>>(
                        () => ExchangeAsync(tenantId!, delegationId!, CancellationToken.None),
                        LazyThreadSafetyMode.ExecutionAndPublication);
                });

            CachedToken? token;
            try
            {
                // Each caller waits under its own token instead.
                token = await entry.Value.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // This caller gave up. The shared exchange stays in flight for everyone else, so
                // the entry must survive.
                throw;
            }
            catch (Exception ex)
            {
                // A faulted Lazy caches its exception forever, so drop the entry and let the next call retry.
                RemoveIf(delegationId!, entry);
                DelegatedTokenProviderLog.ExchangeFailed(_logger, ex);
                return null;
            }

            if (token is not null && token.IsUsable(_timeProvider.GetUtcNow()))
            {
                return token.AccessToken;
            }

            RemoveIf(delegationId!, entry);

            if (createdHere)
            {
                return null;
            }
        }

        return null;
    }

    public async Task<Dictionary<string, string>> GetAuthorizationHeadersAsync(
        Dictionary<string, string>? existingHeaders = null,
        CancellationToken ct = default)
    {
        // Copy: the caller's dictionary is theirs and may be reused across requests.
        var headers = existingHeaders is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(existingHeaders, StringComparer.OrdinalIgnoreCase);

        // A caller-supplied credential always wins.
        if (headers.ContainsKey(BlocksConstants.AuthorizationHeaderName))
        {
            return headers;
        }

        var token = await GetTokenAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return headers;
        }

        headers[BlocksConstants.AuthorizationHeaderName] = BlocksConstants.Bearer + token;

        // Set explicitly: HttpService only forwards headers the caller passed in.
        var tenantId = BlocksContext.GetContext()?.TenantId;
        if (!string.IsNullOrWhiteSpace(tenantId) && !headers.ContainsKey(BlocksConstants.BlocksKey))
        {
            headers[BlocksConstants.BlocksKey] = tenantId!;
        }

        return headers;
    }

    public void Invalidate(string? delegationGrantId)
    {
        if (string.IsNullOrWhiteSpace(delegationGrantId)) return;
        _cache.TryRemove(delegationGrantId!, out _);
    }

    private async Task<CachedToken?> ExchangeAsync(string tenantId, string delegationId, CancellationToken ct)
    {
        // This is the one outbound call that does not go through HttpService -- it cannot, or
        // redeeming a grant would recurse into redeeming a grant -- so it carries its own span.
        // Exchange latency and rate are the metrics that tell you whether delegation is healthy.
        using var activity = _activitySource?.StartActivity(
            "blocks.delegation.token_exchange",
            ActivityKind.Client,
            Activity.Current?.Context ?? default);

        activity?.SetTag("blocks.tenant_id", tenantId);

        var salt = _tenants.GetTenantByID(tenantId)?.TenantSalt;
        if (string.IsNullOrWhiteSpace(salt))
        {
            DelegatedTokenProviderLog.NoTenantSalt(_logger, tenantId);
            return null;
        }

        var endpoint = await _endpointResolver.GetTokenEndpointAsync(tenantId, ct).ConfigureAwait(false);

        var ts = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var nonce = DelegationSignature.NewNonce();
        var signature = DelegationSignature.Compute(tenantId, delegationId, nonce, ts, salt!);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = DelegationConstants.TokenExchangeGrantType,
            ["subject_token"] = delegationId,
            ["subject_token_type"] = DelegationConstants.DelegationGrantTokenType,
            ["nonce"] = nonce,
            ["ts"] = ts.ToString(),
            ["sig"] = signature
        };

        using var client = _httpClientFactory.CreateClient(DelegationConstants.ExchangeHttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.TryAddWithoutValidation(BlocksConstants.BlocksKey, tenantId);

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        activity?.SetTag("http.response.status_code", (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            // The OAuth error code is safe to log and tag. The grant id is neither.
            var error = ReadErrorCode(body);
            activity?.SetTag("blocks.oauth_error", error);
            activity?.SetStatus(ActivityStatusCode.Error, error);

            DelegatedTokenProviderLog.ExchangeRejected(_logger, (int)response.StatusCode, error);
            return null;
        }

        activity?.SetStatus(ActivityStatusCode.Ok);
        return ReadToken(body);
    }

    private CachedToken? ReadToken(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var accessToken = ReadString(root, "access_token") ?? ReadString(root, "accessToken") ?? ReadString(root, "AccessToken");
            if (string.IsNullOrWhiteSpace(accessToken)) return null;

            var expiresIn = ReadInt(root, "expires_in") ?? ReadInt(root, "expiresIn") ?? ReadInt(root, "ExpiresIn") ?? 0;
            if (expiresIn <= 0)
            {
                // A response with no lifetime gets a deliberately short one: twice the renewal
                // margin, so it is servable for the margin's length and then refetched. Trusting
                // it for longer would risk presenting a token IAM has already expired.
                expiresIn = (int)DelegationConstants.TokenRenewalMargin.TotalSeconds * 2;
            }

            return new CachedToken(accessToken!, _timeProvider.GetUtcNow().AddSeconds(expiresIn));
        }
        catch (JsonException ex)
        {
            DelegatedTokenProviderLog.ExchangeUnreadable(_logger, ex);
            return null;
        }
    }

    private void RemoveIf(string key, Lazy<Task<CachedToken?>> expected)
    {
        // Only remove the entry we actually observed, so a competing refresh is not discarded.
        if (_cache.TryGetValue(key, out var current) && ReferenceEquals(current, expected))
        {
            _cache.TryRemove(new KeyValuePair<string, Lazy<Task<CachedToken?>>>(key, expected));
        }
    }

    private void SweepExpired()
    {
        var now = _timeProvider.GetUtcNow();

        foreach (var pair in _cache)
        {
            var lazy = pair.Value;
            if (!lazy.IsValueCreated) continue;

            var task = lazy.Value;
            if (!task.IsCompleted) continue;

            var stale = task.IsFaulted
                || task.IsCanceled
                || task.Result is null
                || !task.Result!.IsUsable(now);

            if (stale)
            {
                RemoveIf(pair.Key, lazy);
            }
        }
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadInt(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static string ReadErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return ReadString(document.RootElement, "error") ?? "unknown";
        }
        catch (JsonException)
        {
            return "unknown";
        }
    }
}

internal static partial class DelegatedTokenProviderLog
{
    [LoggerMessage(EventId = 7020, Level = LogLevel.Warning, Message = "A delegation grant is in scope but BlocksContext carries no tenant; no delegated token will be issued.")]
    public static partial void NoTenantInContext(ILogger logger);

    [LoggerMessage(EventId = 7021, Level = LogLevel.Error, Message = "No tenant salt available for tenant {TenantId}; cannot sign a token exchange.")]
    public static partial void NoTenantSalt(ILogger logger, string tenantId);

    [LoggerMessage(EventId = 7022, Level = LogLevel.Warning, Message = "Token exchange rejected with HTTP {StatusCode} ({Error}).")]
    public static partial void ExchangeRejected(ILogger logger, int statusCode, string error);

    [LoggerMessage(EventId = 7023, Level = LogLevel.Error, Message = "Token exchange failed.")]
    public static partial void ExchangeFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7024, Level = LogLevel.Error, Message = "Token exchange response could not be parsed.")]
    public static partial void ExchangeUnreadable(ILogger logger, Exception exception);
}
