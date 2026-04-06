using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenTelemetry;
using StackExchange.Redis;
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

        public static void JwtBearerAuthentication(this IServiceCollection services)
        {
            services.AddHttpClient();

            ConfigureAuthentication(services);
            ConfigureAuthorization(services);
        }

        private static void ConfigureAuthentication(IServiceCollection services)
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = async context =>
                        {
                            var tenants = context.HttpContext.RequestServices.GetRequiredService<ITenants>();
                            var cacheDb = context.HttpContext.RequestServices.GetRequiredService<ICacheClient>().CacheDatabase();
                            var httpClientFactory = context.HttpContext.RequestServices.GetRequiredService<IHttpClientFactory>();

                            var tokenResult = TokenHelper.GetToken(context.Request, tenants);
                            SetRequestAccessToken(context.HttpContext, tokenResult.Token);

                            if (string.IsNullOrWhiteSpace(tokenResult.Token))
                            {
                                return;
                            }

                            var tenantId = TenantContextHelper.ResolveTenantId(context.Request, tokenResult.Token);
                            SetRequestTenantId(context.HttpContext, tenantId);
                            TenantContextHelper.EnsureTenantContext(context.HttpContext, tenantId);

                            if (tokenResult.IsThirdPartyToken)
                            {
                                await TryFallbackAsync(new TokenValidatedContext(context.HttpContext, context.Scheme, context.Options),
                                                       tenants,
                                                       tokenResult.Token,
                                                       tenantId,
                                                       httpClientFactory);
                                return;
                            }

                            context.Token = tokenResult.Token;
                            await ConfigureTokenValidationAsync(context, tenants, httpClientFactory, tenantId);
                        },

                        OnTokenValidated = context =>
                        {
                            var tenants = context.HttpContext.RequestServices.GetRequiredService<ITenants>();
                            var result = TokenHelper.GetToken(context.Request, tenants);
                            if (context.Principal?.Identity is ClaimsIdentity claimsIdentity)
                            {
                                HandleTokenIssuer(claimsIdentity, context.Request.GetDisplayUrl(), result.Token);
                                StoreBlocksContextInActivity(BlocksContext.CreateFromClaimsIdentity(claimsIdentity));
                            }
                            return Task.CompletedTask;
                        },

                        OnAuthenticationFailed = async context =>
                        {
                            var tenants = context.HttpContext.RequestServices.GetRequiredService<ITenants>();
                            var httpClientFactory = context.HttpContext.RequestServices.GetRequiredService<IHttpClientFactory>();
                            
                            var ex = context.Exception;

                            if (ex is SecurityTokenExpiredException)
                            {
                                SecurityLog(context.HttpContext, "token_expired", "Fallback skipped for expired token.");
                                return;
                            }

                            SecurityLog(context.HttpContext, "authentication_failed", "Primary token validation failed, attempting fallback.", ex);
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
                            SecurityLog(context.HttpContext, "forbidden", "Authorization failed with forbidden response.");
                            return Task.CompletedTask;
                        }
                    };
                });
        }

        private static async Task ConfigureTokenValidationAsync(
            MessageReceivedContext context,
            ITenants tenants,
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

            var cacheDb = context.HttpContext.RequestServices.GetRequiredService<ICacheClient>().CacheDatabase();
            
            var certificate = await GetCertificateAsync(tenantId, tenants, cacheDb, httpClientFactory, context.HttpContext);
            if (certificate == null)
            {
                context.Fail("❌ Certificate not found");
                return;
            }

            var validationParams = tenants.GetTenantByID(tenantId)?.JwtTokenParameters;
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
                SecurityLog(context.HttpContext, "fallback_triggered", $"Triggered due to {ex.GetType().Name}: {ex.Message}", ex);

            try
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    SecurityLog(context.HttpContext, "fallback_no_token", "No token found in request for fallback.");
                    return false;
                }

                tenantId ??= TenantContextHelper.ResolveTenantId(context.Request, token);
                if (string.IsNullOrWhiteSpace(tenantId))
                {
                    SecurityLog(context.HttpContext, "fallback_missing_tenant_context", "Tenant context is missing for fallback validation.");
                    return false;
                }

                var tenant = tenants.GetTenantByID(tenantId);
                if (tenant?.ThirdPartyJwtTokenParameters == null)
                {
                    SecurityLog(context.HttpContext, "fallback_missing_tenant_config", "Tenant or third-party token parameters are missing.");
                    return false;
                }

                var fallbackValidationParams = !string.IsNullOrWhiteSpace(tenant.ThirdPartyJwtTokenParameters.JwksUrl) ?
                                                await GetFromJwksUrl(tenant, httpClientFactory) :
                                                await GetFromPublicCertificate(context.HttpContext, tenant, tenantId, httpClientFactory);

                return await ValidateTokenWithFallbackAsync(token, fallbackValidationParams, context);
            }
            catch (Exception finalEx)
            {
                SecurityLog(context.HttpContext, "fallback_unhandled_exception", "Unhandled fallback exception.", finalEx);
                return false;
            }
        }

        private static void SecurityLog(HttpContext httpContext, string eventName, string message, Exception? ex = null)
        {
            var logger = GetAuthLogger(httpContext);
            if (ex == null)
            {
                logger.LogWarning("Auth event {EventName}: {Message}", eventName, message);
                return;
            }

            logger.LogWarning(ex, "Auth event {EventName}: {Message}", eventName, message);
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


        private static async Task<TokenValidationParameters> GetFromPublicCertificate(HttpContext httpContext, Tenant tenant, string tenantId, IHttpClientFactory httpClientFactory)
        {
            var cert = await GetThirdPartyCertificateAsync(httpContext, tenant, tenantId, httpClientFactory);
            if (cert == null)
            {
                GetAuthLogger(httpContext).LogWarning("Fallback certificate not found for tenant {TenantId}", tenantId);
                return new TokenValidationParameters();
            }

            var validationParams = tenant.ThirdPartyJwtTokenParameters;
            if (validationParams == null)
            {
                GetAuthLogger(httpContext).LogWarning("Fallback validation parameters not found for tenant {TenantId}", tenantId);
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

        private static async Task<bool> ValidateTokenWithFallbackAsync(string token, TokenValidationParameters validationParams, TokenValidatedContext context)
        {
            try
            {
                var handler = new JwtSecurityTokenHandler();
                var validatedPrincipal = handler.ValidateToken(token, validationParams, out _);

                if (validatedPrincipal.Identity is ClaimsIdentity claimsIdentity)
                {
                    HandleTokenIssuer(claimsIdentity, context.Request.GetDisplayUrl(), token);
                    await StoreThirdPartyBlocksContextActivity(claimsIdentity, context);
                }

                context.Principal = validatedPrincipal;
                context.HttpContext.User = validatedPrincipal;
                context.Success();
                return true;
            }
            catch (Exception ex)
            {
                GetAuthLogger(context.HttpContext).LogWarning(ex, "Fallback token validation failed");
                return false;
            }
        }

        private static async Task<X509Certificate2?> GetThirdPartyCertificateAsync(HttpContext httpContext, Tenant tenant, string tenantId, IHttpClientFactory httpClientFactory)
        {
            var certificateData = await LoadCertificateDataAsync(tenant.ThirdPartyJwtTokenParameters?.PublicCertificatePath, httpClientFactory, httpContext);
            return certificateData == null
                ? null
                : CreateCertificate(certificateData, tenant.ThirdPartyJwtTokenParameters.PublicCertificatePassword);
        }

        private static async Task<X509Certificate2?> GetCertificateAsync(string tenantId, ITenants tenants, IDatabase cacheDb, IHttpClientFactory httpClientFactory, HttpContext httpContext)
        {
            string cacheKey = $"{BlocksConstants.TenantTokenPublicCertificateCachePrefix}{tenantId}";

            var cachedCertificate = await cacheDb.StringGetAsync(cacheKey);
            var validationParams = tenants.GetTenantByID(tenantId)?.JwtTokenParameters;

            if (cachedCertificate.HasValue)
                return CreateCertificate(cachedCertificate, validationParams?.PublicCertificatePassword);

            if (validationParams == null || string.IsNullOrWhiteSpace(validationParams.PublicCertificatePath))
                return null;

            var certificateData = await LoadCertificateDataAsync(validationParams.PublicCertificatePath, httpClientFactory, httpContext);
            if (certificateData == null)
                return null;

            await CacheCertificateAsync(cacheDb, cacheKey, certificateData, validationParams);
            return CreateCertificate(certificateData, validationParams.PublicCertificatePassword);
        }

        private static async Task<byte[]?> LoadCertificateDataAsync(string path, IHttpClientFactory httpClientFactory, HttpContext httpContext)
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
                GetAuthLogger(httpContext).LogWarning(e, "Failed to load certificate from {CertificatePath}", path);
                return null;
            }
        }

        private static ILogger GetAuthLogger(HttpContext httpContext)
        {
            var loggerFactory = httpContext.RequestServices?.GetService<ILoggerFactory>();
            return loggerFactory?.CreateLogger("Blocks.Genesis.Auth.JwtBearerAuthenticationExtension")
                   ?? NullLogger.Instance;
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
                SaveSigninToken = true
            };
        }

        private static void StoreBlocksContextInActivity(BlocksContext context)
        {
            Baggage.SetBaggage("UserId", context.UserId);
            Baggage.SetBaggage("IsAuthenticate", "true");

            var activity = Activity.Current;
            var sanitized = GetContextWithoutToken(context);

            activity?.SetTag("SecurityContext", JsonSerializer.Serialize(sanitized));
        }

        private static BlocksContext GetContextWithoutToken(BlocksContext context)
        {
            return BlocksContext.Create(
                tenantId: context.TenantId,
                roles: context.Roles ?? [],
                userId: context.UserId ?? string.Empty,
                isAuthenticated: context.IsAuthenticated,
                requestUri: context.RequestUri ?? string.Empty,
                organizationId: context.OrganizationId ?? string.Empty,
                expireOn: context.ExpireOn,
                email: context.Email ?? string.Empty,
                permissions: context.Permissions ?? [],
                userName: context.UserName ?? string.Empty,
                phoneNumber: context.PhoneNumber ?? string.Empty,
                displayName: context.DisplayName ?? string.Empty,
                oauthToken: string.Empty,
                refreshToken: string.Empty,
                actualTentId: context.TenantId);
        }

        private static void HandleTokenIssuer(ClaimsIdentity identity, string requestUri, string token)
        {
            identity.AddClaims(
            [
                new Claim(BlocksContext.REQUEST_URI_CLAIM, requestUri),
                new Claim(BlocksContext.TOKEN_CLAIM, token)
            ]);
        }

        private static async Task StoreThirdPartyBlocksContextActivity(ClaimsIdentity identity, TokenValidatedContext context)
        {
            _ = context.Request.Headers.TryGetValue(BlocksConstants.BlocksKey, out var apiKey);
            var dbContext = context.HttpContext.RequestServices.GetRequiredService<IDbContextProvider>();
            var claimsMapper = await (await dbContext.GetCollection<BsonDocument>("ThirdPartyJWTClaims").FindAsync(Builders<BsonDocument>.Filter.Empty)).FirstOrDefaultAsync();

            var roleClaim = identity?.FindAll(identity.RoleClaimType).Select(r => r.Value).ToArray() ?? [];

            if (roleClaim.Length == 0)
            {
                roleClaim = ExtractRolesFromClaim(identity, claimsMapper);
            }

            var subClaim = identity?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
            var emailClaim = identity?.FindFirst(ClaimTypes.Email)?.Value ?? string.Empty;

            BlocksContext.SetContext(BlocksContext.Create(
                tenantId: apiKey,
                roles: roleClaim,

                userId: ExtactClaimProperty(claimsMapper["UserId"].ToString() ?? "") == "sub"? subClaim + "_external" :
                        ExtactClaimValue(identity, claimsMapper["UserId"].ToString() ?? "") + "_external",

                isAuthenticated: identity.IsAuthenticated,
                requestUri: context.Request.Host.ToString(),
                organizationId: string.Empty,
                expireOn: DateTime.TryParse(identity.FindFirst("exp")?.Value, out var exp)
                          ? exp : DateTime.MinValue,

                email: !string.IsNullOrWhiteSpace(emailClaim)? emailClaim: 
                       ExtactClaimValue(identity, claimsMapper["Email"]?.ToString() ?? ""),

                permissions: [],
                userName: claimsMapper["UserName"]?.ToString().ToLower() == "email"? emailClaim:
                          ExtactClaimValue(identity, claimsMapper["UserName"]?.ToString() ?? ""),

                phoneNumber: string.Empty,
                displayName: ExtactClaimValue(identity, claimsMapper["Name"]?.ToString() ?? ""),
                oauthToken: identity.FindFirst("oauth")?.Value,
                refreshToken: string.Empty,
                actualTentId: apiKey));

            context.Request.Headers[BlocksConstants.ThirdPartyContextHeader] = JsonSerializer.Serialize(BlocksContext.GetContext());
        }

        private static string ExtactClaimProperty(string claimObject)
        {
            return claimObject.Split('.').Last();
        }

        private static string GetClaimObjectName(string claimObject)
        {
            return claimObject.Split('.').First();
        }

       private static string ExtactClaimValue(ClaimsIdentity identity, string claimObject)
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
            if (identity == null)
                return [];

            var claimName = GetClaimObjectName(claimsMapper["Roles"]?.ToString() ?? "");
            var claimValue = identity.Claims.FirstOrDefault(c => c.Type == claimName)?.Value;

            if (string.IsNullOrWhiteSpace(claimValue))
                return [];

            try
            {
                using var doc = JsonDocument.Parse(claimValue);

                var propertyName = ExtactClaimProperty(claimsMapper["Roles"]?.ToString() ?? "").ToString();

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
