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
            await TryFallbackAsync(new TokenValidatedContext(context.HttpContext, context.Scheme, context.Options),
                                   tenants,
                                   tokenResult.Token,
                                   tenantId,
                                   httpClientFactory).ConfigureAwait(true);
            return;
        }

        context.Token = tokenResult.Token;
        await ConfigureTokenValidationAsync(context, tenants, cacheDb, httpClientFactory, tenantId).ConfigureAwait(true);
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
            new TokenValidatedContext(context.HttpContext, context.Scheme, context.Options),
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
        TokenValidatedContext context,
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

        var accepted = await RunFallbackAsync(context, tenants, token, tenantId, httpClientFactory);

        if (!accepted)
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

    private static async Task<bool> RunFallbackAsync(
        TokenValidatedContext context,
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
                return false;
            }

            tenantId ??= await TenantContextHelper.ResolveTenantIdAsync(context.Request, token);
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                SecurityLog(
                    "fallback_missing_tenant_context",
                    "No tenant could be resolved for this request. Send the tenant as the x-blocks-key header " +
                    "or tenant_id header/query value, or issue the token with a tenant_id claim.",
                    isWarning: true);
                return false;
            }

            var tenant = tenants.GetTenantByID(tenantId);
            if (tenant?.ThirdPartyJwtTokenParameters == null)
            {
                SecurityLog(
                    "fallback_missing_tenant_config",
                    "This tenant has no ThirdPartyJwtTokenParameters, so third-party tokens cannot be accepted for it. " +
                    "If it was just configured, the cached tenant may be stale -- a direct database edit does not " +
                    "invalidate the tenant cache.",
                    detail: new { tenantId, tenantFound = tenant != null },
                    isWarning: true);
                return false;
            }

            return await ValidateTokenWithFallbackAsync(token, tenant, context, httpClientFactory);
        }
        catch (Exception finalEx)
        {
            SecurityLog("fallback_unhandled_exception", "Unhandled exception during third-party validation.", finalEx, isWarning: true);
            return false;
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

    private static async Task<TokenValidationParameters> GetFromJwksUrl(Tenant tenant, IHttpClientFactory httpClientFactory)
    {
        var httpClient = httpClientFactory.CreateClient();
        var jwks = await httpClient.GetFromJsonAsync<JsonWebKeySet>(tenant.ThirdPartyJwtTokenParameters.JwksUrl);

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = !string.IsNullOrWhiteSpace(tenant.ThirdPartyJwtTokenParameters.Issuer),
            ValidIssuer = tenant.ThirdPartyJwtTokenParameters.Issuer,
            ValidateAudience = tenant.ThirdPartyJwtTokenParameters.Audiences?.Count > 0,
            ValidateLifetime = true,
            ValidAudiences = tenant.ThirdPartyJwtTokenParameters.Audiences,
            IssuerSigningKeys = jwks!.Keys
        };

        return parameters;
    }


    private static async Task<TokenValidationParameters> GetFromPublicCertificate(Tenant tenant, IHttpClientFactory httpClientFactory)
    {
        var cert = await GetThirdPartyCertificateAsync(tenant, httpClientFactory);
        if (cert == null)
        {
            SecurityLog(
                "fallback_certificate_missing",
                "Neither JwksUrl nor a readable PublicCertificatePath is configured for this tenant's third-party " +
                "provider, so there is no key to verify the token signature with.",
                isWarning: true);
            return new TokenValidationParameters();
        }

        // A non-null certificate implies ThirdPartyJwtTokenParameters was set,
        // since the certificate path is read from it.
        var validationParams = tenant.ThirdPartyJwtTokenParameters;

        var parameters = CreateTokenValidationParameters(cert, new JwtTokenParameters
        {
            Issuer = validationParams.Issuer,
            Audiences = validationParams.Audiences,
            PrivateCertificatePassword = "",
            IssueDate = DateTime.UtcNow,
        });

        return parameters;
    }

    private static async Task<bool> ValidateTokenWithFallbackAsync(string token, Tenant tenant, TokenValidatedContext context, IHttpClientFactory httpClientFactory)
    {
        try
        {
            var validationParams = !string.IsNullOrWhiteSpace(tenant.ThirdPartyJwtTokenParameters.JwksUrl) ?
                                            await GetFromJwksUrl(tenant, httpClientFactory) :
                                            await GetFromPublicCertificate(tenant, httpClientFactory);
            var handler = new JwtSecurityTokenHandler();
            var validatedPrincipal = handler.ValidateToken(token, validationParams, out _);

            if (validatedPrincipal.Identity is ClaimsIdentity claimsIdentity)
            {
                HandleTokenIssuer(claimsIdentity, context.Request.GetDisplayUrl(), string.Empty);
                await StoreThirdPartyBlocksContextActivity(claimsIdentity, context, tenant);
            }

            context.Principal = validatedPrincipal;
            context.HttpContext.User = validatedPrincipal;
            context.Success();

            SecurityLog(
                "fallback_token_validated",
                "Third-party token signature, issuer and lifetime validated.",
                detail: new { issuer = tenant.ThirdPartyJwtTokenParameters.Issuer, provider = tenant.ThirdPartyJwtTokenParameters.ProviderName });
            return true;
        }
        catch (Exception ex)
        {
            SecurityLog(
                "fallback_validation_failed",
                "Third-party validation did not complete. A JsonException or KeyNotFoundException here is a claim " +
                "MAPPING fault, not a token fault -- the token itself may be perfectly valid.",
                ex,
                detail: new { issuer = tenant.ThirdPartyJwtTokenParameters?.Issuer, jwksUrl = tenant.ThirdPartyJwtTokenParameters?.JwksUrl },
                isWarning: true);
            return false;
        }
    }

    private static async Task<X509Certificate2?> GetThirdPartyCertificateAsync(Tenant tenant, IHttpClientFactory httpClientFactory)
    {
        var certificateData = await LoadCertificateDataAsync(tenant.ThirdPartyJwtTokenParameters?.PublicCertificatePath ?? string.Empty, httpClientFactory);
        return certificateData == null
            ? null
            : CreateCertificate(certificateData, tenant.ThirdPartyJwtTokenParameters?.PublicCertificatePassword);
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

    private static async Task StoreThirdPartyBlocksContextActivity(ClaimsIdentity identity, TokenValidatedContext context, Tenant tenant)
    {
        var dbContext = context.HttpContext.RequestServices.GetRequiredService<IDbContextProvider>();
        var claimsMapper = await (await dbContext.GetCollection<BsonDocument>("ThirdPartyJWTClaims").FindAsync(Builders<BsonDocument>.Filter.Empty)).FirstOrDefaultAsync();

        if (claimsMapper == null)
        {
            SecurityLog(
                "third_party_claims_mapper_missing",
                "No ThirdPartyJWTClaims document exists in this tenant's database, so no Blocks context can be built. " +
                "The token was accepted, so this does not surface as a 401 -- the request proceeds with an empty " +
                "context and fails at the first database call instead.",
                isWarning: true);
            return;
        }

        // The single most useful line when a mapping misbehaves: what was configured, next to what
        // the token actually carries. Read through GetValue so a partially-filled mapper document
        // is reported rather than throwing from inside the logging itself.
        SecurityLog(
            "third_party_claim_mapping",
            "Applying the configured claim mapping to the token.",
            detail: new
            {
                mapping = new
                {
                    UserId = claimsMapper.GetValue("UserId", "").ToString(),
                    Email = claimsMapper.GetValue("Email", "").ToString(),
                    UserName = claimsMapper.GetValue("UserName", "").ToString(),
                    Name = claimsMapper.GetValue("Name", "").ToString(),
                    Roles = claimsMapper.GetValue("Roles", "").ToString()
                },
                tokenClaims = identity.Claims.Select(c => c.Type).Distinct().ToArray()
            });

        var roleClaim = identity.FindAll(identity.RoleClaimType).Select(r => r.Value).ToArray();

        if (roleClaim.Length == 0)
        {
            roleClaim = ExtractRolesFromClaim(identity, claimsMapper);
        }

        var userIdMapping = claimsMapper["UserId"]?.ToString() ?? string.Empty;
        var subClaim = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
        var emailClaim = identity.FindFirst(ClaimTypes.Email)?.Value ?? string.Empty;

        // "sub" names the standard subject claim, which the handler has already mapped to
        // NameIdentifier. Compared whole: a claim named "x.sub" is a different claim, and
        // matching on the last dot-segment used to conflate the two.
        var resolvedSubject = userIdMapping == "sub"
            ? subClaim
            : ResolveMappedClaim(identity, "UserId", userIdMapping);

        var resolvedEmail = !string.IsNullOrWhiteSpace(emailClaim)
            ? emailClaim
            : ResolveMappedClaim(identity, "Email", claimsMapper["Email"]?.ToString() ?? string.Empty);

        var resolvedUserName = claimsMapper["UserName"]?.ToString()?.ToLower() == "email"
            ? emailClaim
            : ResolveMappedClaim(identity, "UserName", claimsMapper["UserName"]?.ToString() ?? string.Empty);

        var resolvedDisplayName = ResolveMappedClaim(identity, "Name", claimsMapper["Name"]?.ToString() ?? string.Empty);

        if (string.IsNullOrWhiteSpace(resolvedSubject))
        {
            // Not a rejection -- the context is still built, and the principal becomes the bare
            // suffix "_external", which every user with a broken mapping collapses onto. Logged
            // loudly because nothing downstream will complain about it.
            SecurityLog(
                "third_party_subject_missing",
                "The UserId mapping resolved to nothing. The principal will be the bare suffix \"_external\", " +
                "which is shared by every token whose subject cannot be resolved. Fix the UserId mapping.",
                detail: new { mapping = userIdMapping },
                isWarning: true);
        }

        var origin = context.Request.Headers.Origin.FirstOrDefault();
        var referer = context.Request.Headers.Referer.FirstOrDefault();


        var applicationDomain = TenantContextHelper.ResolveApplicationDomain(tenant, origin, referer);


        var mappedContext = BlocksContext.Create(
            tenantId: tenant.ItemId,
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
            originalTenantId: tenant.ItemId,
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
                tenantId = tenant.ItemId,
                userId = mappedContext.UserId,
                roles = roleClaim,
                emailResolved = !string.IsNullOrWhiteSpace(resolvedEmail),
                userNameResolved = !string.IsNullOrWhiteSpace(resolvedUserName),
                displayNameResolved = !string.IsNullOrWhiteSpace(resolvedDisplayName)
            });

        context.Request.Headers[BlocksConstants.ThirdPartyContextHeader] =
            JsonSerializer.Serialize(BlocksContext.CreateSanitizedForTransport(mappedContext));

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
    public static string[] ExtractRolesFromClaim(ClaimsIdentity identity, BsonDocument claimsMapper)
    {
        if (identity == null || claimsMapper == null)
            return [];

        var rolesMapping = claimsMapper["Roles"]?.ToString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(rolesMapping))
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
