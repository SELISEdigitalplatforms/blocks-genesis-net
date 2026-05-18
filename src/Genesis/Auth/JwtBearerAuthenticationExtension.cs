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

namespace Blocks.Genesis
{
    internal static class JwtBearerAuthenticationExtension
    {
        private const string RequestAccessTokenItemKey = "blocks.auth.accessToken";
        private const string RequestTenantIdItemKey = "blocks.auth.tenantId";
        private static ITenants? _compatTenants;
        private static IDatabase? _compatCacheDb;
        private static IHttpClientFactory? _compatHttpClientFactory;

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
                        OnMessageReceived = async context =>
                        {
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
                        },

                        OnTokenValidated = async context =>
                        {
                            BlocksHttpContextAccessor.EnsureInitialized(context.HttpContext);

                            var tenants = ResolveTenants(context.HttpContext);

                            if (context.Principal?.Identity is ClaimsIdentity claimsIdentity)
                            {
                                if (!HasServiceAccess(claimsIdentity, context.HttpContext))
                                {
                                    context.Fail("service_access_denied");

                                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                                    context.Response.ContentType = "application/json";

                                    await context.Response.WriteAsJsonAsync(new
                                    {
                                        success = false,
                                        error = new
                                        {
                                            code = "SERVICE_ACCESS_DENIED",
                                            message = "You do not have permission to access this service."
                                        }
                                    }).ConfigureAwait(true);

                                    return;
                                }

                                HandleTokenIssuer(
                                    claimsIdentity,
                                    context.Request.GetDisplayUrl(),
                                    string.Empty);

                                StoreBlocksContextInActivity(
                                    BlocksContext.CreateFromClaimsIdentity(claimsIdentity));
                            }
                        },

                        OnAuthenticationFailed = async context =>
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
                        },

                        OnForbidden = context =>
                        {
                            SecurityLog("forbidden", "Authorization failed with forbidden response.");
                            return Task.CompletedTask;
                        }
                    };
                });
        }

        private static ITenants ResolveTenants(HttpContext context)
        {
            return context.RequestServices?.GetService<ITenants>()
                ?? _compatTenants
                ?? throw new ArgumentNullException("provider");
        }

        private static IDatabase ResolveCacheDatabase(HttpContext context)
        {
            return context.RequestServices?.GetService<ICacheClient>()?.CacheDatabase()
                ?? _compatCacheDb
                ?? throw new ArgumentNullException("provider");
        }

        private static IHttpClientFactory ResolveHttpClientFactory(HttpContext context)
        {
            return context.RequestServices?.GetService<IHttpClientFactory>()
                ?? _compatHttpClientFactory
                ?? throw new ArgumentNullException("provider");
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
                SecurityLog("fallback_triggered", $"Triggered due to {ex.GetType().Name}: {ex.Message}", ex);

            try
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    SecurityLog("fallback_no_token", "No token found in request for fallback.");
                    return false;
                }

                tenantId ??= await TenantContextHelper.ResolveTenantIdAsync(context.Request, token);
                if (string.IsNullOrWhiteSpace(tenantId))
                {
                    SecurityLog("fallback_missing_tenant_context", "Tenant context is missing for fallback validation.");
                    return false;
                }

                var tenant = tenants.GetTenantByID(tenantId);
                if (tenant?.ThirdPartyJwtTokenParameters == null)
                {
                    SecurityLog("fallback_missing_tenant_config", "Tenant or third-party token parameters are missing.");
                    return false;
                }

                return await ValidateTokenWithFallbackAsync(token, tenant, context, httpClientFactory);
            }
            catch (Exception finalEx)
            {
                SecurityLog("fallback_unhandled_exception", "Unhandled fallback exception.", finalEx);
                return false;
            }
        }

        private static void SecurityLog(string eventName, string message, Exception? ex = null)
        {
            var payload = new
            {
                category = "auth",
                eventName,
                message,
                exceptionType = ex?.GetType().Name
            };

            Log.Information("[Security] {Payload}", JsonSerializer.Serialize(payload));
        }

        private static bool HasServiceAccess(ClaimsIdentity identity, HttpContext httpContext)
        {
            var requiredResource = ApplicationConfigurations.ServiceAccessResourceName;

            if (string.IsNullOrWhiteSpace(requiredResource))
            {
                return true;
            }

            return identity.FindAll(BlocksContext.SERVICE_ACCESS_CLAIM)
                .Select(claim => claim.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Any(resource => string.Equals(resource, requiredResource, StringComparison.OrdinalIgnoreCase));
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


        private static async Task<TokenValidationParameters> GetFromPublicCertificate(Tenant tenant, string tenantId, IHttpClientFactory httpClientFactory)
        {
            var cert = await GetThirdPartyCertificateAsync(tenant, tenantId, httpClientFactory);
            if (cert == null)
            {
                Log.Warning("[Fallback] No fallback certificate found.");
                return new TokenValidationParameters();
            }

            var validationParams = tenant.ThirdPartyJwtTokenParameters;
            if (validationParams == null)
            {
                Log.Warning("[Fallback] No validation parameters found.");
                return new TokenValidationParameters();
            }

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
                                                await GetFromPublicCertificate(tenant, tenant.TenantId, httpClientFactory);
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

                Log.Information("[Fallback] Token validated via fallback certificate.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Fallback] Validation failed.");
                return false;
            }
        }

        private static async Task<X509Certificate2?> GetThirdPartyCertificateAsync(Tenant tenant, string tenantId, IHttpClientFactory httpClientFactory)
        {
            var certificateData = await LoadCertificateDataAsync(tenant.ThirdPartyJwtTokenParameters?.PublicCertificatePath, httpClientFactory);
            return certificateData == null
                ? null
                : CreateCertificate(certificateData, tenant.ThirdPartyJwtTokenParameters.PublicCertificatePassword);
        }

        private static async Task<X509Certificate2?> GetCertificateAsync(string tenantId, ITenants tenants, IDatabase cacheDb, IHttpClientFactory httpClientFactory)
        {
            string cacheKey = $"{BlocksConstants.TenantTokenPublicCertificateCachePrefix}{tenantId}";

            var cachedCertificate = await cacheDb.StringGetAsync(cacheKey);
            var validationParams = tenants.GetTenantTokenValidationParameter(tenantId);

            if (cachedCertificate.HasValue)
                return CreateCertificate(cachedCertificate, validationParams?.PublicCertificatePassword);

            if (validationParams == null || string.IsNullOrWhiteSpace(validationParams.PublicCertificatePath))
                return null;

            var certificateData = await LoadCertificateDataAsync(validationParams.PublicCertificatePath, httpClientFactory);
            if (certificateData == null)
                return null;

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

                return File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
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

        private static void StoreBlocksContextInActivity(BlocksContext context)
        {
            Baggage.SetBaggage("UserId", context.UserId);
            Baggage.SetBaggage("IsAuthenticate", "true");

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
            var actualTenantId = await TenantContextHelper.ResolveTenantIdAsync(context.Request);
            var dbContext = context.HttpContext.RequestServices.GetRequiredService<IDbContextProvider>();
            var claimsMapper = await (await dbContext.GetCollection<BsonDocument>("ThirdPartyJWTClaims").FindAsync(Builders<BsonDocument>.Filter.Empty)).FirstOrDefaultAsync();
            
            if (claimsMapper == null)
            {
                Log.Warning("[ThirdParty] Claims mapper not found in database.");
                return;
            }

            var roleClaim = identity?.FindAll(identity.RoleClaimType).Select(r => r.Value).ToArray() ?? [];

            if (roleClaim.Length == 0)
            {
                roleClaim = ExtractRolesFromClaim(identity, claimsMapper);
            }

            var subClaim = identity?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
            var emailClaim = identity?.FindFirst(ClaimTypes.Email)?.Value ?? string.Empty;
            var origin = context.Request.Headers.Origin.FirstOrDefault();
            var referer = context.Request.Headers.Referer.FirstOrDefault();


            var applicationDomain = TenantContextHelper.ResolveApplicationDomain(tenant, origin, referer);


            var mappedContext = BlocksContext.Create(
                tenantId: actualTenantId,
                roles: roleClaim,

                userId: ExtractClaimProperty(claimsMapper["UserId"].ToString() ?? "") == "sub"? subClaim + "_external" :
                        ExtractClaimValue(identity, claimsMapper["UserId"].ToString() ?? "") + "_external",

                isAuthenticated: identity.IsAuthenticated,
                requestUri: context.Request.Host.ToString(),
                organizationId: string.Empty,
                expireOn: DateTime.TryParse(identity.FindFirst("exp")?.Value, out var exp)
                          ? exp : DateTime.MinValue,

                email: !string.IsNullOrWhiteSpace(emailClaim)? emailClaim: 
                       ExtractClaimValue(identity, claimsMapper["Email"]?.ToString() ?? ""),

                permissions: [],
                userName: claimsMapper["UserName"]?.ToString().ToLower() == "email"? emailClaim:
                          ExtractClaimValue(identity, claimsMapper["UserName"]?.ToString() ?? ""),

                phoneNumber: string.Empty,
                displayName: ExtractClaimValue(identity, claimsMapper["Name"]?.ToString() ?? ""),
                oauthToken: string.Empty,
                actualTenantId: actualTenantId,
                applicationDomain: applicationDomain);

            BlocksContext.SetContext(mappedContext);
            context.Request.Headers[BlocksConstants.ThirdPartyContextHeader] =
                JsonSerializer.Serialize(BlocksContext.CreateSanitizedForTransport(mappedContext));

        }

        private static string ExtractClaimProperty(string claimObject)
        {
            return claimObject.Split('.').Last();
        }

        private static string GetClaimObjectName(string claimObject)
        {
            return claimObject.Split('.').First();
        }

       private static string ExtractClaimValue(ClaimsIdentity identity, string claimObject)
        {
            var nestedClaims = claimObject.Split(".");

            if (nestedClaims.Length > 1)
            {
                var claim = identity?.Claims.FirstOrDefault(c => c.Type == nestedClaims[0]?.ToString());
                var claimAccessJson = claim?.Value;
                using var doc = JsonDocument.Parse(claimAccessJson ?? "");
                return doc.RootElement.GetProperty(nestedClaims[1]).ToString();
            }

            return identity.FindFirst(nestedClaims[0])?.Value ?? string.Empty;
        }

        public static string[] ExtractRolesFromClaim(ClaimsIdentity identity, BsonDocument claimsMapper)
        {
            if (identity == null || claimsMapper == null)
                return [];

            var claimName = GetClaimObjectName(claimsMapper["Roles"]?.ToString() ?? "");
            var claimValue = identity.Claims.FirstOrDefault(c => c.Type == claimName)?.Value;

            if (string.IsNullOrWhiteSpace(claimValue))
                return [];

            try
            {
                using var doc = JsonDocument.Parse(claimValue);

                var propertyName = ExtractClaimProperty(claimsMapper["Roles"]?.ToString() ?? "").ToString();

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
}
