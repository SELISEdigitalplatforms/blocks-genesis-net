using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry;
using StackExchange.Redis;
using System.Diagnostics;
using System.Dynamic;
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
                            context.Token = TokenHelper.GetToken(context.Request);

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
                            var token = TokenHelper.GetToken(context.Request);
                            var claimsIdentity = context.Principal.Identity as ClaimsIdentity;
                            HandleTokenIssuer(claimsIdentity, context.Request.GetDisplayUrl(), token);
                            StoreBlocksContextInActivity(BlocksContext.CreateFromClaimsIdentity(claimsIdentity));
                            return Task.CompletedTask;
                        },
                        OnAuthenticationFailed = context =>
                        {
                            Console.WriteLine("Authentication failed: " + context.Exception.Message);
                            return Task.CompletedTask;
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
                ValidateIssuer = true,
                ValidIssuer = validationParams?.Issuer,
                ValidAudiences = validationParams?.Audiences,
                ValidateAudience = true,
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
