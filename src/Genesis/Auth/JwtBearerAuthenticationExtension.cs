using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.DependencyInjection;
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
        public static void JwtBearerAuthentication(this IServiceCollection services)
        {
            var serviceProvider = services.BuildServiceProvider();
            var tenants = serviceProvider.GetRequiredService<ITenants>();
            var cacheDb = serviceProvider.GetRequiredService<ICacheClient>().CacheDatabase();

            BlocksHttpContextAccessor.Init(serviceProvider);

            ConfigureAuthentication(services, tenants, cacheDb);
            ConfigureAuthorization(services);
        }

        private static void ConfigureAuthentication(IServiceCollection services, ITenants tenants, IDatabase cacheDb)
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    string accessToken = "";

                    options.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = async context =>
                        {
                            var tokenResult = TokenHelper.GetToken(context.Request, tenants);
                            accessToken = tokenResult.Token;

                            if (tokenResult.IsThirdPartyToken)
                            {
                                await TryFallbackAsync(new TokenValidatedContext(context.HttpContext, context.Scheme, context.Options),
                                                       tenants,
                                                       accessToken);
                                return;
                            }

                            context.Token = tokenResult.Token;
                            await ConfigureTokenValidationAsync(context, tenants, cacheDb);
                        },

                        OnTokenValidated = context =>
                        {
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
                            var ex = context.Exception;

                            if (ex is SecurityTokenExpiredException)
                            {
                                Console.WriteLine("⚠ Token expired — fallback skipped.");
                                return;
                            }

                            await TryFallbackAsync(new TokenValidatedContext(context.HttpContext, context.Scheme, context.Options), tenants, accessToken, ex);
                        },

                        OnForbidden = context =>
                        {
                            Console.WriteLine("🚫 Authorization failed: Forbidden");
                            return Task.CompletedTask;
                        }
                    };
                });
        }

        private static async Task ConfigureTokenValidationAsync(MessageReceivedContext context, ITenants tenants, IDatabase cacheDb)
        {
            var bc = BlocksContext.GetContext();

            var certificate = await GetCertificateAsync(bc.TenantId, tenants, cacheDb);
            if (certificate == null)
            {
                context.Fail("❌ Certificate not found");
                return;
            }

            var validationParams = tenants.GetTenantTokenValidationParameter(bc.TenantId);
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

        public static async Task<bool> TryFallbackAsync(TokenValidatedContext context, ITenants tenants, string token, Exception? ex = null)
        {
            if (ex != null)
                Console.WriteLine($"[Fallback] Triggered due to: {ex.GetType().Name} - {ex.Message}");

            try
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    Console.WriteLine("[Fallback] ❌ No token found in request.");
                    return false;
                }

                var bc = BlocksContext.GetContext();
                var tenant = tenants.GetTenantByID(bc.TenantId);
                var fallbackValidationParams = !string.IsNullOrWhiteSpace(tenant.ThirdPartyJwtTokenParameters.JwksUrl) ?
                                                await GetFromJwksUrl(tenant, bc) :
                                                await GetFromPublicCertificate(tenant, bc);

                return await ValidateTokenWithFallbackAsync(token, fallbackValidationParams, context);
            }
            catch (Exception finalEx)
            {
                Console.WriteLine($"[Fallback] 💥 Unhandled error: {finalEx}");
                return false;
            }
        }

        private static async Task<TokenValidationParameters> GetFromJwksUrl(Tenant tenant, BlocksContext bc)
        {
            using var httpClient = new HttpClient();
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


        private static async Task<TokenValidationParameters> GetFromPublicCertificate(Tenant tenant, BlocksContext bc)
        {
            var cert = await GetThirdPartyCertificateAsync(tenant, bc.TenantId);
            if (cert == null)
            {
                Console.WriteLine("[Fallback] ❌ No fallback certificate found.");
                return new TokenValidationParameters();
            }

            var validationParams = tenant.ThirdPartyJwtTokenParameters;
            if (validationParams == null)
            {
                Console.WriteLine("[Fallback] ❌ No validation parameters found.");
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

                Console.WriteLine("[Fallback] ✅ Token validated via fallback certificate.");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Fallback] ❌ Validation failed: {ex.Message}");
                return false;
            }
        }

        private static async Task<X509Certificate2?> GetThirdPartyCertificateAsync(Tenant tenant, string tenantId)
        {
            var certificateData = await LoadCertificateDataAsync(tenant.ThirdPartyJwtTokenParameters?.PublicCertificatePath);
            return certificateData == null
                ? null
                : CreateCertificate(certificateData, tenant.ThirdPartyJwtTokenParameters.PublicCertificatePassword);
        }

        private static async Task<X509Certificate2?> GetCertificateAsync(string tenantId, ITenants tenants, IDatabase cacheDb)
        {
            string cacheKey = $"{BlocksConstants.TenantTokenPublicCertificateCachePrefix}{tenantId}";

            var cachedCertificate = await cacheDb.StringGetAsync(cacheKey);
            var validationParams = tenants.GetTenantTokenValidationParameter(tenantId);

            if (cachedCertificate.HasValue)
                return CreateCertificate(cachedCertificate, validationParams?.PublicCertificatePassword);

            if (validationParams == null || string.IsNullOrWhiteSpace(validationParams.PublicCertificatePath))
                return null;

            var certificateData = await LoadCertificateDataAsync(validationParams.PublicCertificatePath);
            if (certificateData == null)
                return null;

            await CacheCertificateAsync(cacheDb, cacheKey, certificateData, validationParams);
            return CreateCertificate(certificateData, validationParams.PublicCertificatePassword);
        }

        private static async Task<byte[]?> LoadCertificateDataAsync(string path)
        {
            try
            {
                if (Uri.IsWellFormedUriString(path, UriKind.Absolute))
                {
                    using var httpClient = new HttpClient();
                    return await httpClient.GetByteArrayAsync(path);
                }

                return File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[Cert] ❌ Failed to load certificate: {e.Message}");
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
                var claim = identity?.Claims.FirstOrDefault(c => c.Type == GetClaimObjectName(claimsMapper["Roles"]?.ToString() ?? ""));
                var roleAccessJson = claim?.Value;
                using var doc = JsonDocument.Parse(roleAccessJson ?? "");
                roleClaim = doc.RootElement.GetProperty(ExtactClaimProperty(claimsMapper["Roles"]?.ToString() ?? "").ToString()).EnumerateArray().Select(x => x.GetString()).ToArray() ?? [];
            }

            var subClaim = identity?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
            var emailClaim = identity?.FindFirst(ClaimTypes.Email)?.Value ?? string.Empty;

            BlocksContext.SetContext(BlocksContext.Create(
                tenantId: apiKey,
                roles: roleClaim,

                userId: ExtactClaimProperty(claimsMapper["UserId"].ToString() ?? "") == "sub"? subClaim:
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
    }
}
