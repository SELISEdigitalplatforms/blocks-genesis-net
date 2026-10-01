using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Blocks.Genesis;

/// <summary>
/// Two-step resolution, per section 5.5 of the delegated-access spec.
/// <list type="number">
/// <item>
/// Discovery (primary): <c>GET {BLOCKS_IAM_BASE_URL}/{tenantId}/.well-known/openid-configuration</c>
/// and take <c>token_endpoint</c>. Survives prefix and route changes.
/// </item>
/// <item>
/// <c>BLOCKS_IAM_TOKEN_ENDPOINT</c> (fallback): a complete URL, not a base and not a template,
/// so no prefix is ever guessed.
/// </item>
/// </list>
/// A successful discovery is cached per tenant. A fallback is not cached, so discovery is retried
/// lazily on later calls. If neither is configured, startup fails.
/// <para>
/// <c>Tenant.JwtTokenParameters.Issuer</c> is deliberately not used as a base URL: IAM separates
/// <c>issuer</c> from <c>apiBase</c>, and the issuer is an identifier, not a reachable API host.
/// </para>
/// </summary>
public sealed class DelegationTokenEndpointResolver : IDelegationTokenEndpointResolver
{
    private readonly IConfiguration? _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DelegationTokenEndpointResolver> _logger;

    private readonly ConcurrentDictionary<string, string> _discovered = new(StringComparer.Ordinal);
    private int _fallbackWarningLogged;

    public DelegationTokenEndpointResolver(
        IHttpClientFactory httpClientFactory,
        ILogger<DelegationTokenEndpointResolver> logger,
        IConfiguration? configuration = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _configuration = configuration;
    }

    public void EnsureConfigured()
    {
        if (!string.IsNullOrWhiteSpace(ResolveBaseUrl()) || !string.IsNullOrWhiteSpace(ResolveConfiguredEndpoint()))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Delegated access is unconfigured: set '{DelegationConstants.IamBaseUrlKey}' (preferred, used for OIDC " +
            $"discovery) or '{DelegationConstants.IamTokenEndpointKey}' (a complete token endpoint URL). " +
            "Both must point at IAM's internal address, never the public host.");
    }

    public async Task<string> GetTokenEndpointAsync(string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new InvalidOperationException("Cannot resolve the IAM token endpoint without a tenant.");
        }

        if (_discovered.TryGetValue(tenantId, out var cached))
        {
            return cached;
        }

        var discovered = await TryDiscoverAsync(tenantId, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(discovered))
        {
            _discovered[tenantId] = discovered!;
            return discovered!;
        }

        var configured = ResolveConfiguredEndpoint();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Interlocked.Exchange(ref _fallbackWarningLogged, 1) == 0)
            {
                DelegationEndpointLog.UsingConfiguredEndpoint(_logger, DelegationConstants.IamTokenEndpointKey);
            }

            return configured!;
        }

        throw new InvalidOperationException(
            $"OIDC discovery failed for tenant '{tenantId}' and '{DelegationConstants.IamTokenEndpointKey}' is not " +
            "configured. Refusing to guess the token endpoint path.");
    }

    private async Task<string?> TryDiscoverAsync(string tenantId, CancellationToken ct)
    {
        var baseUrl = ResolveBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;

        var url = $"{baseUrl!.TrimEnd('/')}/{tenantId}/.well-known/openid-configuration";

        try
        {
            using var client = _httpClientFactory.CreateClient(DelegationConstants.ExchangeHttpClientName);
            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                DelegationEndpointLog.DiscoveryFailed(_logger, (int)response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(payload);

            if (!document.RootElement.TryGetProperty("token_endpoint", out var endpoint))
            {
                DelegationEndpointLog.DiscoveryMissingEndpoint(_logger);
                return null;
            }

            var value = endpoint.GetString();
            return Uri.TryCreate(value, UriKind.Absolute, out _) ? value : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            DelegationEndpointLog.DiscoveryUnreachable(_logger, ex);
            return null;
        }
    }

    private string? ResolveBaseUrl() => ResolveSetting(DelegationConstants.IamBaseUrlKey);

    private string? ResolveConfiguredEndpoint() => ResolveSetting(DelegationConstants.IamTokenEndpointKey);

    /// <summary>
    /// Environment variable, then the <c>FrontendRuntime</c> section, then the configuration root.
    /// <para>
    /// <c>FrontendRuntime</c> comes before the root because that is where Blocks services put their
    /// runtime settings — it is the expected home for these keys, not a last resort. A bare root-level
    /// key still works, so an <c>appsettings.json</c> that sets it either way resolves.
    /// </para>
    /// </summary>
    private string? ResolveSetting(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(value)) return value.Trim();

        if (_configuration is null) return null;

        value = _configuration.GetSection(DelegationConstants.FrontendRuntimeSection)[key];
        if (!string.IsNullOrWhiteSpace(value)) return value.Trim();

        value = _configuration[key];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

internal static partial class DelegationEndpointLog
{
    [LoggerMessage(EventId = 7010, Level = LogLevel.Warning, Message = "OIDC discovery for the IAM token endpoint returned {StatusCode}.")]
    public static partial void DiscoveryFailed(ILogger logger, int statusCode);

    [LoggerMessage(EventId = 7011, Level = LogLevel.Warning, Message = "OIDC discovery document has no token_endpoint.")]
    public static partial void DiscoveryMissingEndpoint(ILogger logger);

    [LoggerMessage(EventId = 7012, Level = LogLevel.Warning, Message = "OIDC discovery for the IAM token endpoint was unreachable.")]
    public static partial void DiscoveryUnreachable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7013, Level = LogLevel.Warning, Message = "Falling back to the configured token endpoint '{Key}'. Discovery will be retried lazily.")]
    public static partial void UsingConfiguredEndpoint(ILogger logger, string key);
}
