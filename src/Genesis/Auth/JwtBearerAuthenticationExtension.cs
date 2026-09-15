using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenTelemetry;
using StackExchange.Redis;
using Serilog;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Blocks.Genesis;

public static class JwtBearerAuthenticationExtension
{
    private const string RequestAccessTokenItemKey = "blocks.auth.accessToken";
    private const string RequestTenantIdItemKey = "blocks.auth.tenantId";

    public static void JwtBearerAuthentication(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        BlocksHttpContextAccessor.Instance ??= new HttpContextAccessor();
        services.AddHttpClient();
        ConfigureAuthenticationInternal(services);
        ConfigureAuthorization(services);
    }

    private static void ConfigureAuthenticationInternal(IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = HandleMessageReceivedAsync,

                    OnTokenValidated = HandleTokenValidatedAsync,

                    OnAuthenticationFailed = HandleAuthenticationFailedAsync,

                    OnForbidden = context =>
                    {
                        SecurityLog("forbidden", "Authorization failed with forbidden response.");
                        return Task.CompletedTask;
                    }
                };
            });
    }

    private static async Task HandleMessageReceivedAsync(MessageReceivedContext context)
    {
        var endpoint = context.HttpContext.GetEndpoint();
        var allowsAnonymous = endpoint?.Metadata?.GetMetadata<IAllowAnonymous>() != null;
        var hasAuthorizationMetadata = endpoint?.Metadata?.GetMetadata<IAuthorizeData>() != null;
        if (allowsAnonymous || !hasAuthorizationMetadata)
        {
            // Skip JWT parsing/validation for public endpoints.
            // Public means either [AllowAnonymous] or no auth attribute at all.
            return;
        }

        BlocksHttpContextAccessor.EnsureInitialized(context.HttpContext);
        var tenants = ResolveTenants(context.HttpContext);
        var cacheDb = ResolveCacheDatabase(context.HttpContext);
        var httpClientFactory = ResolveHttpClientFactory(context.HttpContext);
        var tokenResult = TokenHelper.GetToken(context.Request, tenants);
        SetRequestAccessToken(context.HttpContext, tokenResult.Token);

        if (string.IsNullOrWhiteSpace(tokenResult.Token))
        {
            return;
        }

        var tenantId = await TenantContextHelper.ResolveTenantIdAsync(context.Request, tokenResult.Token).ConfigureAwait(true);
        SetRequestTenantId(context.HttpContext, tenantId);
        var tenant = tenantId != null ? tenants.GetTenantByID(tenantId) : null;
        TenantContextHelper.EnsureTenantContext(context.HttpContext, tenant);

        if (tokenResult.IsThirdPartyToken)
        {
            await TryFallbackAsync(context,
                                   tenants,
                                   tokenResult.Token,
                                   tenantId,
                                   httpClientFactory).ConfigureAwait(true);
            return;
        }

        // A tenant that has opted in routes on the token's own issuer: one minted by a configured
        // provider is validated as third-party, and anything else -- a Blocks token above all --
        // falls straight through to primary validation without a wasted attempt.
        if (tenant?.IsThirdPartyJwtEnabled == true)
        {
            var acceptedAsThirdParty = await TryFallbackAsync(
                context,
                tenants,
                tokenResult.Token,
                tenantId,
                httpClientFactory).ConfigureAwait(true);

            if (acceptedAsThirdParty)
            {
                return;
            }

            // Not ours, or ours and broken. Either way primary validation gets its turn, so a
            // provider token caught mid key-rotation is not locked out by one failure.
        }

        context.Token = tokenResult.Token;
        await ConfigureTokenValidationAsync(context, tenants, cacheDb, httpClientFactory, tenantId).ConfigureAwait(true);
    }

    /// <summary>
    /// Reads <c>iss</c> and <c>aud</c> without validating anything.
    /// </summary>
    /// <remarks>
    /// Safe because these only select a key set: validation then re-checks both against the chosen
    /// provider, so a forged issuer routes to a configuration whose keys cannot verify the
    /// signature. An unreadable token yields nothing and therefore selects no provider.
    /// </remarks>
    private static (string Issuer, IReadOnlyCollection<string> Audiences) ReadTokenRouting(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();

            if (!handler.CanReadToken(token))
            {
                return (string.Empty, []);
            }

            var jwt = handler.ReadJwtToken(token);
            return (jwt.Issuer ?? string.Empty, jwt.Audiences?.ToArray() ?? []);
        }
        catch (Exception)
        {
            return (string.Empty, []);
        }
    }

    private static async Task HandleTokenValidatedAsync(TokenValidatedContext context)
    {
        BlocksHttpContextAccessor.EnsureInitialized(context.HttpContext);

        _ = ResolveTenants(context.HttpContext);

        if (context.Principal?.Identity is ClaimsIdentity claimsIdentity)
        {
            HandleTokenIssuer(
                claimsIdentity,
                context.Request.GetDisplayUrl(),
                string.Empty);

            StoreBlocksContextInActivity(
                BlocksContext.CreateFromClaimsIdentity(claimsIdentity));
        }
    }

    private static async Task HandleAuthenticationFailedAsync(AuthenticationFailedContext context)
    {
        BlocksHttpContextAccessor.EnsureInitialized(context.HttpContext);
        var tenants = ResolveTenants(context.HttpContext);
        var httpClientFactory = ResolveHttpClientFactory(context.HttpContext);
        var ex = context.Exception;

        if (ex is SecurityTokenExpiredException)
        {
            SecurityLog("token_expired", "Fallback skipped for expired token.");
            return;
        }

        SecurityLog("authentication_failed", "Primary token validation failed, attempting fallback.", ex);
        await TryFallbackAsync(
            context,
            tenants,
            GetRequestAccessToken(context.HttpContext),
            GetRequestTenantId(context.HttpContext),
            httpClientFactory,
            ex);
    }

    private static ITenants ResolveTenants(HttpContext context)
    {
        return context.RequestServices?.GetService<ITenants>()
            ?? throw new InvalidOperationException("ITenants service could not be resolved from the request services.");
    }

    private static IDatabase ResolveCacheDatabase(HttpContext context)
    {
        return context.RequestServices?.GetService<ICacheClient>()?.CacheDatabase()
            ?? throw new InvalidOperationException("The cache database could not be resolved from the request services.");
    }

    private static IHttpClientFactory ResolveHttpClientFactory(HttpContext context)
    {
        return context.RequestServices?.GetService<IHttpClientFactory>()
            ?? throw new InvalidOperationException("IHttpClientFactory service could not be resolved from the request services.");
    }

    private static async Task ConfigureTokenValidationAsync(
        MessageReceivedContext context,
        ITenants tenants,
        IDatabase cacheDb,
        IHttpClientFactory httpClientFactory,
        string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            if (string.IsNullOrWhiteSpace(context.Token))
            {
                return;
            }

            context.Fail("❌ Tenant context not found");
            return;
        }

        var certificate = await GetCertificateAsync(tenantId, tenants, cacheDb, httpClientFactory);
        if (certificate == null)
        {
            context.Fail("❌ Certificate not found");
            return;
        }

        var validationParams = tenants.GetTenantTokenValidationParameter(tenantId);
        if (validationParams == null)
        {
            context.Fail("❌ Validation parameters not found");
            return;
        }

        context.Options.TokenValidationParameters = CreateTokenValidationParameters(certificate, validationParams);
    }

    private static void ConfigureAuthorization(IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy("Protected", policy => policy.Requirements.Add(new ProtectedEndpointAccessRequirement()))
            .AddPolicy("Secret", policy => policy.Requirements.Add(new SecretEndPointRequirement()));

        services.AddScoped<IAuthorizationHandler, ProtectedEndpointAccessHandler>();
        services.AddScoped<IAuthorizationHandler, SecretAuthorizationHandler>();
    }

    public static async Task<bool> TryFallbackAsync(
        ResultContext<JwtBearerOptions> context,
        ITenants tenants,
        string token,
        string? tenantId,
        IHttpClientFactory httpClientFactory,
        Exception? ex = null)
    {
        if (ex != null)
            // The quoted exception is the PRIMARY validation failing against this tenant's own
            // Blocks certificate. For a third-party token that is expected -- it is the trigger
            // for this fallback, not the reason any eventual 401 happened. Said outright because
            // its "Issuer did not match SeliseBlocks" text reliably sends people chasing issuer
            // configuration that was never wrong.
            SecurityLog(
                "fallback_triggered",
                "Primary validation rejected the token, so third-party validation is being attempted. " +
                "The quoted exception is expected for a third-party token and is NOT the cause of any 401 " +
                "that follows -- look for the fallback_* or third_party_* event after this one.",
                ex);

        var (accepted, outcome) = await RunFallbackAsync(context, tenants, token, tenantId, httpClientFactory);

        // "No provider claims this issuer" is the everyday case on an enabled tenant -- every
        // Blocks token looks exactly like that -- so it stays quiet. Logging it as a rejection
        // would flood the channel and desensitise everyone to the events that matter.
        var isRoutineMiss = outcome is ThirdPartyProviderSelection.IssuerUnmatched
                                    or ThirdPartyProviderSelection.NoProviders;

        if (!accepted && !isRoutineMiss)
        {
            // Terminal line. Without it the last thing in the log is ASP.NET's
            // "Bearer was not authenticated. Failure message: IDX10205 ...", which repeats the
            // primary-validation error and reads as though the issuer were misconfigured.
            SecurityLog(
                "fallback_rejected",
                "Third-party token was not accepted; this request will be answered 401. " +
                "The preceding fallback_* / third_party_* event is the actual reason.",
                isWarning: true);
        }

        return accepted;
    }

    private static async Task<(bool Accepted, ThirdPartyProviderSelection Outcome)> RunFallbackAsync(
        ResultContext<JwtBearerOptions> context,
        ITenants tenants,
        string token,
        string? tenantId,
        IHttpClientFactory httpClientFactory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                SecurityLog(
                    "fallback_no_token",
                    "No token was present on the request, so there is nothing to validate.",
                    isWarning: true);
                return (false, ThirdPartyProviderSelection.NoProviders);
            }

            // Resolved from the request alone -- deliberately NOT from the token, and deliberately
            // ignoring any tenant the caller already resolved, which may have come from it.
            //
            // ResolveTenantIdAsync falls back to the token's own tenant_id claim when no header,
            // query or form value carries one. For a Blocks token that is safe: we signed it. For a
            // third-party token it is not, because the claim is written by someone outside this
            // system. Honouring it would let any provider name the tenant whose providers, claim
            // mapping and roles get applied to its token -- so a token from an issuer that one
            // tenant trusts could be pointed at another tenant entirely.
            tenantId = await TenantContextHelper.ResolveTenantIdAsync(context.Request);

            if (string.IsNullOrWhiteSpace(tenantId))
            {
                SecurityLog(
                    "fallback_missing_tenant_context",
                    "No tenant was declared on the request. A third-party token must be accompanied by the " +
                    "x-blocks-key header (or a tenant_id header/query value): the tenant is never taken from " +
                    "the token itself, because the token is minted outside this system.",
                    isWarning: true);
                return (false, ThirdPartyProviderSelection.NoProviders);
            }

            var tenant = tenants.GetTenantByID(tenantId);
            if (tenant is null)
            {
                SecurityLog(
                    "fallback_missing_tenant_config",
                    "The tenant could not be loaded, so its providers cannot be read.",
                    detail: new { tenantId },
                    isWarning: true);
                return (false, ThirdPartyProviderSelection.NoProviders);
            }

            if (!tenant.IsThirdPartyJwtEnabled)
            {
                // Reached through the cookie path, which does not consult the flag first.
                SecurityLog(
                    "third_party_not_enabled",
                    "IsThirdPartyJwtEnabled is off for this tenant, so tokens from external providers are not " +
                    "accepted. Any 'issuer did not match' error above is the expected consequence, not the cause.",
                    detail: new { tenantId },
                    isWarning: true);
                return (false, ThirdPartyProviderSelection.NoProviders);
            }

            var store = context.HttpContext.RequestServices.GetService<IThirdPartyJwtProviderStore>();
            if (store is null)
            {
                SecurityLog(
                    "third_party_provider_store_missing",
                    "IThirdPartyJwtProviderStore is not registered in this host, so no provider can be resolved.",
                    isWarning: true);
                return (false, ThirdPartyProviderSelection.NoProviders);
            }

            var providers = await store.GetActiveAsync(tenantId);
            var routing = ReadTokenRouting(token);
            var headerKey = context.Request.Headers[BlocksConstants.ThirdPartyIdpHeader].FirstOrDefault();

            var selection = ThirdPartyProviderSelector.Select(providers, routing.Issuer, routing.Audiences, headerKey);

            if (!selection.IsSelected)
            {
                ReportSelectionFailure(selection, routing, providers, headerKey);
                return (false, selection.Outcome);
            }

            var provider = selection.Provider!;

            SecurityLog(
                "third_party_provider_selected",
                "Provider selected for this token.",
                detail: new
                {
                    provider.Key,
                    provider.ProviderName,
                    provider.Issuer,
                    algorithms = provider.Algorithms,
                    candidates = selection.CandidateCount
                });

            var validated = await ValidateTokenWithFallbackAsync(token, tenant, provider, context, httpClientFactory);
            return (validated, ThirdPartyProviderSelection.Selected);
        }
        catch (Exception finalEx)
        {
            SecurityLog("fallback_unhandled_exception", "Unhandled exception during third-party validation.", finalEx, isWarning: true);
            return (false, ThirdPartyProviderSelection.Selected);
        }
    }

    /// <summary>
    /// Explains a selection miss in the terms an operator can act on: what the token carried,
    /// beside what is configured.
    /// </summary>
    private static void ReportSelectionFailure(
        ThirdPartyProviderResult selection,
        (string Issuer, IReadOnlyCollection<string> Audiences) routing,
        IReadOnlyList<ThirdPartyJwtProvider> providers,
        string? headerKey)
    {
        var configured = providers
            .Select(p => new { p.Key, p.Issuer, p.Audiences })
            .ToArray();

        var detail = new
        {
            tokenIssuer = routing.Issuer,
            tokenAudiences = routing.Audiences,
            headerKey,
            candidates = selection.CandidateCount,
            configured
        };

        switch (selection.Outcome)
        {
            case ThirdPartyProviderSelection.NoProviders:
                SecurityLog(
                    "third_party_no_providers_configured",
                    "The tenant has third-party tokens enabled but no active provider rows.",
                    detail: detail,
                    isWarning: true);
                break;

            case ThirdPartyProviderSelection.IssuerUnmatched:
                // Routine: this is what every Blocks token looks like. Information, not a fault.
                SecurityLog(
                    "third_party_provider_unmatched",
                    "No configured provider claims this token's issuer, so it is not a third-party token. " +
                    "Primary validation will handle it.",
                    detail: detail);
                break;

            case ThirdPartyProviderSelection.AudienceUnmatched:
                SecurityLog(
                    "third_party_provider_unmatched",
                    "A provider matches this issuer but none accepts the token's audience.",
                    detail: detail,
                    isWarning: true);
                break;

            case ThirdPartyProviderSelection.AmbiguousNoHeader:
                SecurityLog(
                    "third_party_provider_ambiguous",
                    "Several providers share this issuer and audience, so the x-blocks-idp header is required " +
                    "to choose between them. Providers sharing an issuer should differ by audience.",
                    detail: detail,
                    isWarning: true);
                break;

            case ThirdPartyProviderSelection.AmbiguousHeaderUnmatched:
                SecurityLog(
                    "third_party_provider_ambiguous",
                    "The x-blocks-idp header named a provider that is not among the candidates for this token.",
                    detail: detail,
                    isWarning: true);
                break;
        }
    }

    /// <summary>
    /// Single channel for every authentication diagnostic, so one grep for <c>[Security]</c>
    /// returns the whole story of a request. Outcomes used to be split across <c>[Fallback]</c>
    /// and <c>[ThirdParty]</c> prefixes, which meant grepping the obvious one showed the failure
    /// starting and never why it ended.
    /// </summary>
    private static void SecurityLog(string eventName, string message, Exception? ex = null, object? detail = null, bool isWarning = false)
    {
        var payload = new
        {
            category = "auth",
            eventName,
            message,
            exceptionType = ex?.GetType().Name,
            exceptionMessage = ex?.Message,
            detail
        };

        var json = JsonSerializer.Serialize(payload);

        if (isWarning)
            Log.Warning(ex, "[Security] {Payload}", json);
        else
            Log.Information("[Security] {Payload}", json);
    }

    private static void SetRequestAccessToken(HttpContext context, string token)
    {
        context.Items[RequestAccessTokenItemKey] = token;
    }

    private static string GetRequestAccessToken(HttpContext context)
    {
        return context.Items.TryGetValue(RequestAccessTokenItemKey, out var token)
            ? token?.ToString() ?? string.Empty
            : string.Empty;
    }

    private static void SetRequestTenantId(HttpContext context, string? tenantId)
    {
        context.Items[RequestTenantIdItemKey] = tenantId ?? string.Empty;
    }

    private static string? GetRequestTenantId(HttpContext context)
    {
        var tenantId = context.Items.TryGetValue(RequestTenantIdItemKey, out var value)
            ? value?.ToString()
            : null;

        return string.IsNullOrWhiteSpace(tenantId) ? null : tenantId;
    }

    /// <summary>
    /// Validation parameters for a provider, with the key source chosen by the configured
    /// algorithms rather than by anything the token says.
    /// </summary>
    /// <remarks>
    /// <c>ValidAlgorithms</c> is pinned from configuration. Without it the validator accepts
    /// whatever the key type happens to support, which is wider than any provider actually needs
    /// and is the surface algorithm-confusion attacks aim at.
    /// </remarks>
    private static async Task<TokenValidationParameters?> BuildProviderValidationParametersAsync(
        Tenant tenant,
        ThirdPartyJwtProvider provider,
        ResultContext<JwtBearerOptions> context,
        IHttpClientFactory httpClientFactory)
    {
        var algorithms = provider.Algorithms;

        if (!algorithms.IsSingleKeySource())
        {
            SecurityLog(
                "third_party_algorithm_invalid",
                "A provider must configure at least one algorithm and they must all draw their key from the same " +
                "place. A row mixing families, or left Unspecified, cannot be validated safely.",
                detail: new { provider.Key, algorithms },
                isWarning: true);
            return null;
        }

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = !string.IsNullOrWhiteSpace(provider.Issuer),
            ValidIssuer = provider.Issuer,
            ValidateAudience = provider.Audiences?.Count > 0,
            ValidAudiences = provider.Audiences,
            ValidateLifetime = true,
            ValidAlgorithms = algorithms.ToWireNames()
        };

        var isSymmetric = algorithms[0].IsSymmetric();

        if (isSymmetric)
        {
            var key = ResolveSymmetricKey(tenant, provider, context);
            if (key is null)
            {
                return null;
            }

            parameters.IssuerSigningKey = key;
            return parameters;
        }

        if (string.IsNullOrWhiteSpace(provider.JwksUrl))
        {
            SecurityLog(
                "third_party_key_source_missing",
                "This provider uses an asymmetric algorithm but has no JwksUrl, so there is no key to verify with.",
                detail: new { provider.Key, algorithms },
                isWarning: true);
            return null;
        }

        var jwks = await httpClientFactory.CreateClient().GetFromJsonAsync<JsonWebKeySet>(provider.JwksUrl);
        parameters.IssuerSigningKeys = jwks!.Keys;
        return parameters;
    }

    /// <summary>
    /// Decrypts the provider's shared secret into an HMAC key.
    /// </summary>
    /// <remarks>
    /// The key material is the tenant's salt, so the value is readable only alongside the tenant
    /// record. A failure here is a configuration or tampering fault rather than a bad token, and
    /// is reported as its own outcome so the two are never confused in a log.
    /// </remarks>
    private static SymmetricSecurityKey? ResolveSymmetricKey(
        Tenant tenant,
        ThirdPartyJwtProvider provider,
        ResultContext<JwtBearerOptions> context)
    {
        if (string.IsNullOrWhiteSpace(provider.SigningSecretCipher))
        {
            SecurityLog(
                "third_party_key_source_missing",
                "This provider uses an HMAC algorithm but carries no signing secret.",
                detail: new { provider.Key },
                isWarning: true);
            return null;
        }

        var crypto = context.HttpContext.RequestServices.GetService<ICryptoService>();
        if (crypto is null)
        {
            SecurityLog(
                "third_party_secret_undecryptable",
                "ICryptoService is not registered in this host, so the signing secret cannot be decrypted.",
                isWarning: true);
            return null;
        }

        if (string.IsNullOrWhiteSpace(tenant.TenantSalt))
        {
            SecurityLog(
                "third_party_secret_undecryptable",
                "The tenant has no TenantSalt, which is the key material the signing secret was encrypted under.",
                detail: new { provider.Key },
                isWarning: true);
            return null;
        }

        var secret = crypto.Decrypt(provider.SigningSecretCipher, tenant.TenantSalt);

        if (string.IsNullOrWhiteSpace(secret))
        {
            // TenantSalt is load-bearing here: regenerating it makes every stored secret for the
            // tenant undecryptable, and the symptom is a 401 carrying a perfectly valid token.
            SecurityLog(
                "third_party_secret_undecryptable",
                "The stored signing secret did not decrypt. Either it was tampered with, or the tenant salt it " +
                "was encrypted under has changed -- re-save the provider to re-encrypt it.",
                detail: new { provider.Key },
                isWarning: true);
            return null;
        }

        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
    }

    private static async Task<bool> ValidateTokenWithFallbackAsync(
        string token,
        Tenant tenant,
        ThirdPartyJwtProvider provider,
        ResultContext<JwtBearerOptions> context,
        IHttpClientFactory httpClientFactory)
    {
        try
        {
            var validationParams = await BuildProviderValidationParametersAsync(tenant, provider, context, httpClientFactory);

            if (validationParams is null)
            {
                // Already reported with the specific reason; adding a second line would only
                // restate it less precisely.
                return false;
            }

            var handler = new JwtSecurityTokenHandler();
            var validatedPrincipal = handler.ValidateToken(token, validationParams, out _);

            if (validatedPrincipal.Identity is ClaimsIdentity claimsIdentity)
            {
                HandleTokenIssuer(claimsIdentity, context.Request.GetDisplayUrl(), string.Empty);
                var mappedContext = StoreThirdPartyBlocksContextActivity(claimsIdentity, context, tenant, provider);

                if (mappedContext is not null)
                {
                    // The principal is the only carrier that reaches the endpoint. SetContext writes
                    // an AsyncLocal from inside the authentication event's own async subtree, and a
                    // value set there does not flow back out to the middleware that awaited it -- by
                    // the time the controller runs it is gone. GetContext() then falls through to
                    // HttpContext.User, so anything the mapping resolved has to be a claim on this
                    // identity or it is lost, and the first database call fails for want of a tenant.
                    StampBlocksClaims(claimsIdentity, mappedContext);
                    StoreBlocksContextInActivity(mappedContext);
                }
            }

            context.Principal = validatedPrincipal;
            context.HttpContext.User = validatedPrincipal;

            // Sets Result on the event context ASP.NET is holding, which is what stops the handler.
            // Without it the accepted token still falls through to primary validation, fails against
            // the tenant's own Blocks certificate, and the handler answers AuthenticateResult.Fail --
            // the "Bearer was not authenticated" line -- after this method has already said yes.
            context.Success();

            SecurityLog(
                "fallback_token_validated",
                "Third-party token signature, issuer, audience and lifetime validated.",
                detail: new { provider.Key, provider.ProviderName, provider.Issuer });
            return true;
        }
        catch (Exception ex)
        {
            SecurityLog(
                "fallback_validation_failed",
                "Third-party validation did not complete. A JsonException here is a claim MAPPING fault, not a " +
                "token fault -- the token itself may be perfectly valid.",
                ex,
                detail: new { provider.Key, provider.Issuer, provider.JwksUrl },
                isWarning: true);
            return false;
        }
    }

    public static async Task<X509Certificate2?> GetCertificateAsync(string tenantId, ITenants tenants, IDatabase cacheDb, IHttpClientFactory httpClientFactory)
    {
        string cacheKey = $"{BlocksConstants.TenantTokenPublicCertificateCachePrefix}{tenantId}";

        var cachedCertificate = await cacheDb.StringGetAsync(cacheKey);
        var validationParams = tenants.GetTenantTokenValidationParameter(tenantId);

        if (cachedCertificate.HasValue)
            return CreateCertificate(((byte[])cachedCertificate)!, validationParams?.PublicCertificatePassword);

        if (validationParams == null || string.IsNullOrWhiteSpace(validationParams.PublicCertificatePath))
        {
            // Distinguishing these two is what tells an operator whether the tenant document
            // is wrong or the tenant is simply unknown to this process.
            Log.Warning(
                "[Cert] No public certificate path for tenant {TenantId}. CacheMiss=true, JwtTokenParametersPresent={HasParameters}.",
                tenantId,
                validationParams != null);
            return null;
        }

        var certificateData = await LoadCertificateDataAsync(validationParams.PublicCertificatePath, httpClientFactory);
        if (certificateData == null)
        {
            Log.Warning(
                "[Cert] Public certificate could not be loaded for tenant {TenantId} from configured path.",
                tenantId);
            return null;
        }

        await CacheCertificateAsync(cacheDb, cacheKey, certificateData, validationParams);
        return CreateCertificate(certificateData, validationParams.PublicCertificatePassword);
    }

    private static async Task<byte[]?> LoadCertificateDataAsync(string path, IHttpClientFactory httpClientFactory)
    {
        try
        {
            if (Uri.IsWellFormedUriString(path, UriKind.Absolute))
            {
                var httpClient = httpClientFactory.CreateClient();
                return await httpClient.GetByteArrayAsync(path);
            }

            if (File.Exists(path))
            {
                return await File.ReadAllBytesAsync(path);
            }

            // Reached when the stored path is neither an absolute URI nor a local file —
            // a relative or malformed URL lands here and used to return null silently.
            Log.Warning("[Cert] Certificate path is not an absolute URI and no such file exists.");
            return null;
        }
        catch (Exception e)
        {
            Log.Warning(e, "[Cert] Failed to load certificate.");
            return null;
        }
    }

    private static async Task CacheCertificateAsync(IDatabase cacheDb, string cacheKey, byte[] certificateData, JwtTokenParameters validationParams)
    {
        if (validationParams?.IssueDate == null || validationParams.CertificateValidForNumberOfDays <= 0)
            return;

        int daysRemaining = validationParams.CertificateValidForNumberOfDays -
                            (DateTime.UtcNow - validationParams.IssueDate).Days - 1;

        if (daysRemaining > 0)
            await cacheDb.StringSetAsync(cacheKey, certificateData, TimeSpan.FromDays(daysRemaining));
    }

    private static X509Certificate2 CreateCertificate(byte[] data, string? password)
    {
        try
        {
            return X509CertificateLoader.LoadPkcs12(data, password);
        }
        catch
        {
            return X509CertificateLoader.LoadCertificate(data);
        }
    }

    private static TokenValidationParameters CreateTokenValidationParameters(
        X509Certificate2 certificate,
        JwtTokenParameters? parameters)
    {
        return new TokenValidationParameters
        {
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            IssuerSigningKey = new X509SecurityKey(certificate),
            ValidateIssuerSigningKey = true,
            ValidateIssuer = !string.IsNullOrWhiteSpace(parameters?.Issuer),
            ValidIssuer = parameters?.Issuer,
            ValidAudiences = parameters?.Audiences,
            ValidateAudience = parameters?.Audiences?.Count > 0,
            SaveSigninToken = false
        };
    }

    public static void StoreBlocksContextInActivity(BlocksContext context)
    {
        Baggage.SetBaggage("UserId", context.UserId);
        Baggage.SetBaggage("IsAuthenticate", "true");
        Baggage.SetBaggage("TenantId", context.TenantId);
        var activity = Activity.Current;
        var sanitized = BlocksContext.CreateSanitizedForTransport(context);

        activity?.SetTag("SecurityContext", JsonSerializer.Serialize(sanitized));
    }


    private static void HandleTokenIssuer(ClaimsIdentity identity, string requestUri, string token)
    {
        identity.AddClaims(
        [
            new Claim(BlocksContext.REQUEST_URI_CLAIM, requestUri)
        ]);

        if (!string.IsNullOrWhiteSpace(token))
        {
            identity.AddClaim(new Claim(BlocksContext.TOKEN_CLAIM, token));
        }
    }

    /// <summary>
    /// Maps a validated third-party token onto a Blocks context using the provider's own mapping.
    /// </summary>
    private static BlocksContext? StoreThirdPartyBlocksContextActivity(
        ClaimsIdentity identity,
        ResultContext<JwtBearerOptions> context,
        Tenant tenant,
        ThirdPartyJwtProvider provider)
    {
        var mapping = provider.ClaimsMapping;

        if (mapping is null || !mapping.IsConfigured())
        {
            SecurityLog(
                "third_party_claims_mapper_missing",
                "This provider has no claim mapping, so no Blocks context can be built. The token was accepted, " +
                "so this does not surface as a 401 -- the request proceeds with an empty context and fails at the " +
                "first database call instead.",
                detail: new { provider.Key },
                isWarning: true);
            return null;
        }

        // The single most useful line when a mapping misbehaves: what was configured, next to what
        // the token actually carries.
        SecurityLog(
            "third_party_claim_mapping",
            "Applying the provider's claim mapping to the token.",
            detail: new
            {
                provider.Key,
                mapping = new
                {
                    mapping.UserId,
                    mapping.Email,
                    mapping.UserName,
                    mapping.Name,
                    mapping.Roles
                },
                tokenClaims = identity.Claims.Select(c => c.Type).Distinct().ToArray()
            });

        var roleClaim = identity.FindAll(identity.RoleClaimType).Select(r => r.Value).ToArray();

        if (roleClaim.Length == 0)
        {
            roleClaim = ExtractRolesFromClaim(identity, mapping.Roles);
        }

        var subClaim = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
        var emailClaim = identity.FindFirst(ClaimTypes.Email)?.Value ?? string.Empty;

        // "sub" names the standard subject claim, which the handler has already mapped to
        // NameIdentifier. Compared whole: a claim named "x.sub" is a different claim.
        var resolvedSubject = mapping.UserId == "sub"
            ? subClaim
            : ResolveMappedClaim(identity, "UserId", mapping.UserId);

        var resolvedEmail = !string.IsNullOrWhiteSpace(emailClaim)
            ? emailClaim
            : ResolveMappedClaim(identity, "Email", mapping.Email);

        var resolvedUserName = string.Equals(mapping.UserName, "email", StringComparison.OrdinalIgnoreCase)
            ? emailClaim
            : ResolveMappedClaim(identity, "UserName", mapping.UserName);

        var resolvedDisplayName = ResolveMappedClaim(identity, "Name", mapping.Name);

        if (string.IsNullOrWhiteSpace(resolvedSubject))
        {
            // Not a rejection -- the context is still built, and the principal becomes the bare
            // suffix "_external", which every token with a broken mapping collapses onto. Logged
            // loudly because nothing downstream will complain about it.
            SecurityLog(
                "third_party_subject_missing",
                "The UserId mapping resolved to nothing. The principal will be the bare suffix \"_external\", " +
                "which is shared by every token whose subject cannot be resolved. Fix the UserId mapping.",
                detail: new { provider.Key, mapping = mapping.UserId },
                isWarning: true);
        }

        var origin = context.Request.Headers.Origin.FirstOrDefault();
        var referer = context.Request.Headers.Referer.FirstOrDefault();
        var applicationDomain = TenantContextHelper.ResolveApplicationDomain(tenant, origin, referer);

        // TenantId, not ItemId: tenant lookup, the database registry and every other context in
        // the system are keyed by TenantId, so an ItemId here resolves to no database at all.
        var mappedContext = BlocksContext.Create(
            tenantId: tenant.TenantId,
            roles: roleClaim,
            userId: resolvedSubject + "_external",
            isAuthenticated: identity.IsAuthenticated,
            requestUri: context.Request.Host.ToString(),
            organizationId: string.Empty,
            expireOn: DateTime.TryParse(identity.FindFirst("exp")?.Value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var exp)
                      ? exp : DateTime.MinValue,
            email: resolvedEmail,
            permissions: [],
            userName: resolvedUserName,
            phoneNumber: string.Empty,
            displayName: resolvedDisplayName,
            oauthToken: string.Empty,
            originalTenantId: tenant.TenantId,
            applicationDomain: applicationDomain);

        BlocksContext.SetContext(mappedContext);

        // Closes the story: what the mapping actually produced. Email and name are reported as
        // resolved/not rather than by value -- enough to verify a mapping, without putting user
        // identifiers in the log.
        SecurityLog(
            "third_party_context_created",
            "Token mapped to a Blocks context; the request is authenticated.",
            detail: new
            {
                tenantId = tenant.TenantId,
                provider.Key,
                userId = mappedContext.UserId,
                roles = roleClaim,
                emailResolved = !string.IsNullOrWhiteSpace(resolvedEmail),
                userNameResolved = !string.IsNullOrWhiteSpace(resolvedUserName),
                displayNameResolved = !string.IsNullOrWhiteSpace(resolvedDisplayName)
            });

        context.Request.Headers[BlocksConstants.ThirdPartyContextHeader] =
            JsonSerializer.Serialize(BlocksContext.CreateSanitizedForTransport(mappedContext));

        return mappedContext;
    }

    /// <summary>
    /// Writes the mapped values onto the third-party identity as the Blocks claims every consumer
    /// downstream reads.
    /// </summary>
    /// <remarks>
    /// Reserved claims are stripped first. Without that a provider could name its own tenant,
    /// permissions or Blocks user id simply by minting those claims into its token -- after this,
    /// only what the provider's configured mapping resolved survives.
    /// </remarks>
    private static void StampBlocksClaims(ClaimsIdentity identity, BlocksContext blocksContext)
    {
        string[] reserved =
        [
            BlocksContext.TENANT_ID_CLAIM,
            BlocksContext.ORIGINAL_TENANT_ID_CLAIM,
            BlocksContext.USER_ID_CLAIM,
            BlocksContext.USER_NAME_CLAIM,
            BlocksContext.DISPLAY_NAME_CLAIM,
            BlocksContext.EMAIL_CLAIM,
            BlocksContext.PERMISSION_CLAIM,
            BlocksContext.ORGANIZATION_ID_CLAIM,
            BlocksContext.IMPERSONATED_CLAIM,
            BlocksContext.IMPERSONATION_SESSION_ID_CLAIM,
            BlocksContext.CLIENT_ID_CLAIM,
            BlocksContext.ROLES_CLAIM,
            identity.RoleClaimType
        ];

        foreach (var claim in identity.Claims.Where(c => reserved.Contains(c.Type, StringComparer.Ordinal)).ToArray())
        {
            identity.TryRemoveClaim(claim);
        }

        AddClaim(BlocksContext.TENANT_ID_CLAIM, blocksContext.TenantId);
        AddClaim(BlocksContext.ORIGINAL_TENANT_ID_CLAIM, blocksContext.OriginalTenantId);
        AddClaim(BlocksContext.USER_ID_CLAIM, blocksContext.UserId);
        AddClaim(BlocksContext.USER_NAME_CLAIM, blocksContext.UserName);
        AddClaim(BlocksContext.DISPLAY_NAME_CLAIM, blocksContext.DisplayName);
        AddClaim(BlocksContext.EMAIL_CLAIM, blocksContext.Email);

        foreach (var role in blocksContext.Roles ?? [])
        {
            // Written under the identity's own role claim type, which is what
            // CreateFromClaimsIdentity reads them back through.
            AddClaim(identity.RoleClaimType, role);
        }

        void AddClaim(string type, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                identity.AddClaim(new Claim(type, value));
            }
        }
    }

    private static string ExtractClaimProperty(string claimObject)
    {
        return claimObject.Split('.')[^1];
    }

    private static string GetClaimObjectName(string claimObject)
    {
        return claimObject.Split('.')[0];
    }

    /// <summary>
    /// Wraps <see cref="ExtractClaimValue"/> so a mapping that resolves to nothing is reported
    /// against the field it was configured for. <see cref="ExtractClaimValue"/> only knows the
    /// mapping string; "the UserId mapping matched nothing" is what someone reading the log needs.
    /// </summary>
    private static string ResolveMappedClaim(ClaimsIdentity identity, string field, string mapping)
    {
        if (string.IsNullOrWhiteSpace(mapping))
        {
            SecurityLog(
                "third_party_claim_unmapped",
                $"No claim is mapped for {field}, so it will be empty.",
                detail: new { field });
            return string.Empty;
        }

        var value = ExtractClaimValue(identity, mapping);

        if (string.IsNullOrWhiteSpace(value))
        {
            SecurityLog(
                "third_party_claim_unresolved",
                $"The {field} mapping matched no claim in the token, so it will be empty. " +
                "Compare it against tokenClaims in the third_party_claim_mapping event above.",
                detail: new { field, mapping },
                isWarning: true);
        }

        return value;
    }

    /// <summary>
    /// Resolves a configured claim mapping to a single value.
    /// <para>
    /// The mapping is treated as a **literal claim name** first. Claim names are opaque strings and
    /// routinely contain dots -- every namespaced OIDC claim is a URI, e.g.
    /// <c>https://myapp.example.com/user_id</c> from Auth0, Okta or Azure -- so splitting one is
    /// only ever correct when no claim by that exact name exists. The legacy
    /// <c>claim.property</c> form (a claim whose value is a JSON object, as in Keycloak's
    /// <c>realm_access.roles</c>) stays as the fallback so mappings configured before this keep
    /// working untouched.
    /// </para>
    /// <para>
    /// Never throws. This runs inside the third-party token fallback, where an exception aborts
    /// <see cref="ValidateTokenWithFallbackAsync"/> and turns a cryptographically valid token into
    /// a 401 reported as an issuer mismatch. An unresolvable mapping is an empty field.
    /// </para>
    /// </summary>
    private static string ExtractClaimValue(ClaimsIdentity identity, string claimObject)
    {
        if (identity == null || string.IsNullOrWhiteSpace(claimObject))
            return string.Empty;

        var literalClaim = identity.FindFirst(claimObject);
        if (literalClaim != null)
            return literalClaim.Value ?? string.Empty;

        var nestedClaims = claimObject.Split('.');
        if (nestedClaims.Length < 2)
            return string.Empty;

        // Caller (ResolveMappedClaim) reports the miss against its field name, so nothing is
        // logged here -- it would only duplicate that with less context.
        var claimAccessJson = identity.FindFirst(nestedClaims[0])?.Value;
        if (string.IsNullOrWhiteSpace(claimAccessJson))
            return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(claimAccessJson);
            return doc.RootElement.TryGetProperty(nestedClaims[1], out var value)
                 ? value.ToString()
                 : string.Empty;
        }
        catch (JsonException ex)
        {
            SecurityLog(
                "third_party_claim_invalid_json",
                "A mapping used the nested claim.property form, but the claim's value is not JSON.",
                ex,
                detail: new { mapping = claimObject, claim = nestedClaims[0] },
                isWarning: true);
            return string.Empty;
        }
    }

    /// <summary>
    /// Resolves the configured roles mapping to a role list.
    /// <para>
    /// The mapping is tried as a **literal claim name** first.
    /// <see cref="JwtSecurityTokenHandler"/> already flattens a JSON array claim into one
    /// <see cref="Claim"/> per element, so a namespaced Auth0 or Okta roles claim is read straight
    /// off the identity with no JSON parsing at all -- which is why this collects every matching
    /// claim rather than the first. The legacy <c>claim.property</c> form (a claim holding a JSON
    /// object, as in Keycloak's <c>realm_access.roles</c>) remains the fallback.
    /// </para>
    /// <para>
    /// A single scalar claim yields a single role. Splitting a delimited value -- "admin manager"
    /// as one role or two -- cannot be decided by inspection and needs a configured delimiter,
    /// which this mapping does not carry yet.
    /// </para>
    /// </summary>
    public static string[] ExtractRolesFromClaim(ClaimsIdentity identity, string rolesMapping)
    {
        if (identity == null || string.IsNullOrWhiteSpace(rolesMapping))
            return [];

        var literalRoles = identity.FindAll(rolesMapping)
                                   .Select(c => c.Value)
                                   .Where(v => !string.IsNullOrWhiteSpace(v))
                                   .ToArray();

        if (literalRoles.Length > 0)
            return literalRoles;

        var claimName = GetClaimObjectName(rolesMapping);
        var claimValue = identity.FindFirst(claimName)?.Value;

        if (string.IsNullOrWhiteSpace(claimValue))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(claimValue);

            var propertyName = ExtractClaimProperty(rolesMapping);

            if (!doc.RootElement.TryGetProperty(propertyName, out var rolesElement))
                return [];

            if (rolesElement.ValueKind != JsonValueKind.Array)
                return [];

            return rolesElement
                    .EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToArray();
        }
        catch
        {
            return [];
        }
    }
}
