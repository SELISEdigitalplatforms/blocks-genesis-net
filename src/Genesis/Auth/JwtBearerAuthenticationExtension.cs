using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry;
using StackExchange.Redis;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
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

            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = async context =>
                        {
                            var result = TokenHelper.GetToken(context.Request, tenants);

                            if (result.IsThirdPartyToken)
                            {
                                await TryFallbackAsync(new TokenValidatedContext(context.HttpContext, context.Scheme, context.Options), tenants);
                                return;
                            }
                                
                            context.Token = result.Token;
                            var bc = BlocksContext.GetContext();
                            var certificate = await GetCertificateAsync(bc.TenantId, tenants, cacheDb);
                            if (certificate == null)
                            {
                                context.Fail("Certificate not found");
                                return;
                            }

                            var validationParams = tenants.GetTenantTokenValidationParameter(bc.TenantId);
                            if (validationParams == null)
                            {
                                context.Fail("Validation parameters not found");
                                return;
                            }

                            context.Options.TokenValidationParameters = CreateTokenValidationParameters(certificate, validationParams);
                        },
                        OnTokenValidated = context =>
                        {
                            var result = TokenHelper.GetToken(context.Request, tenants);
                            var claimsIdentity = context.Principal.Identity as ClaimsIdentity;
                            HandleTokenIssuer(claimsIdentity, context.Request.GetDisplayUrl(), result.Token);
                            StoreBlocksContextInActivity(BlocksContext.CreateFromClaimsIdentity(claimsIdentity));
                            return Task.CompletedTask;
                        },
                        OnAuthenticationFailed = async context =>
                        {
                            var ex = context.Exception;

                            // Skip fallback for token expiration
                            if (ex is SecurityTokenExpiredException)
                            {
                                Console.WriteLine("Token expired. Fallback skipped.");
                                return ;
                            }

                            // Attempt fallback validation
                            var success = await TryFallbackAsync(new TokenValidatedContext(context.HttpContext, context.Scheme, context.Options),tenants, ex);

                            return ;
                        },
                        OnForbidden = context =>
                        {
                            Console.WriteLine("Authorization failed: Forbidden");
                            return Task.CompletedTask;
                        }
                    };
                });

            services.AddAuthorizationBuilder()
                    .AddPolicy("Protected", policy => policy.Requirements.Add(new ProtectedEndpointAccessRequirement()));
            services.AddAuthorizationBuilder()
                   .AddPolicy("Secret", policy => policy.Requirements.Add(new SecretEndPointRequirement()));

            services.AddScoped<IAuthorizationHandler, ProtectedEndpointAccessHandler>();
            services.AddScoped<IAuthorizationHandler, SecretAuthorizationHandler>();
        }

        public static async Task<bool> TryFallbackAsync(TokenValidatedContext context, ITenants tenants, Exception? ex = null)
        {
            if(ex != null)
                Console.WriteLine($"[Fallback] Triggered due to: {ex?.GetType().Name} - {ex?.Message}");

            try
            {
                var bc = BlocksContext.GetContext();

                // 🔁 1. Attempt to fetch the fallback certificate
                var tenant = tenants.GetTenantByID(bc.TenantId);
                var fallbackCert = await GetClientCertificateAsync(tenant, bc.TenantId);
                if (fallbackCert == null)
                {
                    Console.WriteLine("[Fallback] No fallback certificate found.");
                    return false;
                }

                // 🔐 2. Retrieve tenant-specific token validation parameters
                var validationParams = tenant.ThirdPartyJwtTokenParameters;
                if (validationParams == null)
                {
                    Console.WriteLine("[Fallback] No validation parameters found.");
                    return false;
                }

                // 🧩 3. Create fallback validation parameters using the backup cert
                var fallbackValidationParams = CreateTokenValidationParameters(fallbackCert, new JwtTokenParameters
                {
                    Issuer = validationParams.Issuer,
                    Audiences = validationParams.Audiences,
                    PrivateCertificatePassword = "",
                    IssueDate = DateTime.UtcNow,
                });

                // 🔄 4. Extract token again (it’s the same one that failed earlier)
                var result = TokenHelper.GetToken(context.Request, tenants);
                if (string.IsNullOrWhiteSpace(result.Token))
                {
                    Console.WriteLine("[Fallback] No token present in request.");
                    return false;
                }

                // 🧠 5. Validate the token manually
                var handler = new JwtSecurityTokenHandler();
                ClaimsPrincipal? validatedPrincipal = null;

                try
                {
                    validatedPrincipal = handler.ValidateToken(result.Token, fallbackValidationParams, out var validatedToken);
                    var claimsIdentity = context.Principal.Identity as ClaimsIdentity;
                    HandleTokenIssuer(claimsIdentity, context.Request.GetDisplayUrl(), result.Token);
                    StoreThirdPartyBlocksContextActivity(claimsIdentity, context);
                }
                catch (Exception fallbackEx)
                {
                    Console.WriteLine($"[Fallback] Validation failed: {fallbackEx.Message}");
                    return false;
                }

                // ✅ 6. Set new principal and mark authentication as successful
                context.Principal = validatedPrincipal;
                context.Success();

                Console.WriteLine("[Fallback] Token successfully validated using fallback certificate.");
                return true;
            }
            catch (Exception finalEx)
            {
                Console.WriteLine($"[Fallback] Unhandled error: {finalEx}");
                return false;
            }
        }

        private static async Task<X509Certificate2?> GetClientCertificateAsync(Tenant tenant, string tenantId)
        {

            byte[]? certificateData = await LoadCertificateDataAsync(tenant.ThirdPartyJwtTokenParameters?.PublicCertificatePath);
            if (certificateData == null) return null;

            return CreateCertificate(certificateData, tenant.ThirdPartyJwtTokenParameters.PublicCertificatePassword);
        }



        private static async Task<X509Certificate2?> GetCertificateAsync(string tenantId, ITenants tenants, IDatabase cacheDb)
        {
            string cacheKey = $"{BlocksConstants.TenantTokenPublicCertificateCachePrefix}{tenantId}";
            var cachedCertificate = await cacheDb.StringGetAsync(cacheKey);
            var validationParams = tenants.GetTenantTokenValidationParameter(tenantId);

            if (cachedCertificate.HasValue)
            {
                return CreateCertificate(cachedCertificate, validationParams?.PublicCertificatePassword);
            }

            if (validationParams == null || string.IsNullOrWhiteSpace(validationParams.PublicCertificatePath))
                return null;

            byte[]? certificateData = await LoadCertificateDataAsync(validationParams.PublicCertificatePath);
            if (certificateData == null) return null;

            await CacheCertificateAsync(cacheDb, cacheKey, certificateData, validationParams);
            return CreateCertificate(certificateData, validationParams.PublicCertificatePassword);
        }

        private static async Task<byte[]?> LoadCertificateDataAsync(string certificatePath)
        {
            try
            {
                if (Uri.IsWellFormedUriString(certificatePath, UriKind.Absolute))
                {
                    using var httpClient = new HttpClient();
                    return await httpClient.GetByteArrayAsync(certificatePath);
                }
                return File.Exists(certificatePath) ? await File.ReadAllBytesAsync(certificatePath) : null;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Failed to load certificate: {e.Message}");
                return null;
            }
        }

        private static async Task CacheCertificateAsync(IDatabase cacheDb, string cacheKey, byte[] certificateData, JwtTokenParameters validationParams)
        {
            if (validationParams?.IssueDate == null || validationParams.CertificateValidForNumberOfDays <= 0)
                return;

            int daysRemaining = validationParams.CertificateValidForNumberOfDays - (DateTime.UtcNow - validationParams.IssueDate).Days - 1;
            if (daysRemaining > 0)
            {
                await cacheDb.StringSetAsync(cacheKey, certificateData, TimeSpan.FromDays(daysRemaining));
            }
        }

        private static X509Certificate2 CreateCertificate(byte[] certificateData, string? password)
        {
            try
            {
                // Try to load as PKCS12 (with password if provided)
                return X509CertificateLoader.LoadPkcs12(certificateData, password);
            }
            catch (Exception pkcs12Ex)
            {
                Console.WriteLine($"PKCS12 certificate loading failed: {pkcs12Ex.Message}. Trying fallback loader...");
                try
                {
                    // Fallback: try to load as a standard certificate
                    return X509CertificateLoader.LoadCertificate(certificateData);
                }
                catch (Exception fallbackEx)
                {
                    Console.WriteLine($"Fallback certificate loading failed: {fallbackEx.Message}");
                    throw new InvalidOperationException("Failed to load X509 certificate from provided data.", fallbackEx);
                }
            }
        }


        private static TokenValidationParameters CreateTokenValidationParameters(X509Certificate2 certificate, JwtTokenParameters? validationParams)
        {
            return new TokenValidationParameters
            {
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero,
                IssuerSigningKey = new X509SecurityKey(certificate),
                ValidateIssuerSigningKey = true,
                ValidateIssuer = !string.IsNullOrWhiteSpace(validationParams?.Issuer),
                ValidIssuer = validationParams?.Issuer,
                ValidAudiences = validationParams?.Audiences,
                ValidateAudience = validationParams?.Audiences?.Count > 0,
                SaveSigninToken = true
            };
        }

        private static void StoreBlocksContextInActivity(BlocksContext blocksContext)
        {
            Baggage.SetBaggage("UserId", blocksContext.UserId);
            Baggage.SetBaggage("IsAuthenticate", "true");
            var activity = Activity.Current;

            var contextWithOutToken = GetContextWithOutToken(blocksContext);
            activity?.SetTag("SecurityContext", JsonSerializer.Serialize(contextWithOutToken));
        }

        private static void StoreThirdPartyBlocksContextActivity(ClaimsIdentity claimsIdentity, TokenValidatedContext context)
        {
            var projectKey = context.Request.Headers.TryGetValue(BlocksConstants.BlocksKey, out var apiKey);

            //TODO:
            //Get Token Mapper from Db and adjust the context

            BlocksContext.SetContext(BlocksContext.Create
                (
                   tenantId: apiKey,
                   roles:  Enumerable.Empty<string>(),
                   userId: string.Empty,
                   isAuthenticated: claimsIdentity.IsAuthenticated,
                   requestUri: context.Request.Host.ToString(),
                   organizationId: string.Empty,
                   expireOn: DateTime.TryParse(claimsIdentity.FindFirst("exp")?.Value, out var exp) ? exp : DateTime.MinValue,
                   email: claimsIdentity.FindFirst("email")?.ToString() ?? "",
                   permissions: Enumerable.Empty<string>(),
                   userName: claimsIdentity.FindFirst("email")?.ToString() ?? "",
                   phoneNumber: string.Empty,
                   displayName: string.Empty,
                   oauthToken: claimsIdentity.FindFirst("oauth")?.Value,
                   actualTentId: apiKey
               ));


            var activity = Activity.Current;
        }

        private static BlocksContext GetContextWithOutToken(BlocksContext blocksContext)
        {
            return BlocksContext.Create(tenantId: blocksContext.TenantId,
                                        roles: blocksContext?.Roles ?? [],
                                        userId: blocksContext?.UserId ?? string.Empty,
                                        isAuthenticated: blocksContext?.IsAuthenticated ?? false,
                                        requestUri: blocksContext?.RequestUri ?? string.Empty,
                                        organizationId: blocksContext?.OrganizationId ?? string.Empty,
                                        expireOn: blocksContext?.ExpireOn ?? DateTime.UtcNow.AddHours(1),
                                        email: blocksContext?.Email ?? string.Empty,
                                        permissions: blocksContext?.Permissions ?? [],
                                        userName: blocksContext?.UserName ?? string.Empty,
                                        phoneNumber: blocksContext?.PhoneNumber ?? string.Empty,
                                        displayName: blocksContext?.DisplayName ?? string.Empty,
                                        oauthToken: string.Empty,
                                        actualTentId: blocksContext?.TenantId);
        }


        private static void HandleTokenIssuer(ClaimsIdentity claimsIdentity, string requestUri, string jwtBearerToken)
        {
            var requestClaims = new Claim[]
            {
                new (BlocksContext.REQUEST_URI_CLAIM, requestUri),
                new (BlocksContext.TOKEN_CLAIM, jwtBearerToken)
            };

            claimsIdentity.AddClaims(requestClaims);
        }

    }
}
