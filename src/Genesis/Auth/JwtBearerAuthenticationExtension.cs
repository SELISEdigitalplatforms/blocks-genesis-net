using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;

namespace Blocks.Genesis
{
    internal static class JwtBearerAuthenticationExtension
    {
        public static void JwtBearerAuthentication(this IServiceCollection services)
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = async context =>
                        {
                            context.Token = TokenHelper.GetToken(context.Request);

                            var serviceProvider = context.HttpContext.RequestServices;
                            var tenants = serviceProvider.GetRequiredService<ITenants>();
                            var cacheDb = serviceProvider.GetRequiredService<ICacheClient>().CacheDatabase();

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
                            var claimsIdentity = context.Principal.Identity as ClaimsIdentity;
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

            services.AddAuthorization(options =>
            {
                options.AddPolicy("Protected", policy => policy.Requirements.Add(new ProtectedEndpointAccessRequirement()));
            });

            services.AddScoped<IAuthorizationHandler, ProtectedEndpointAccessHandler>();
        }

        private static async Task<X509Certificate2?> GetCertificateAsync(string tenantId, ITenants tenants, IDatabase cacheDb)
        {
            string cacheKey = $"{BlocksConstants.TenantTokenPublicCertificateCachePrefix}{tenantId}";
            string? cachedCertificate = await cacheDb.StringGetAsync(cacheKey);

            if (!string.IsNullOrEmpty(cachedCertificate))
            {
                return CreateCertificate(Convert.FromBase64String(cachedCertificate), tenants.GetTenantTokenValidationParameter(tenantId)?.PublicCertificatePassword);
            }

            var validationParams = tenants.GetTenantTokenValidationParameter(tenantId);
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
                await cacheDb.StringSetAsync(cacheKey, Convert.ToBase64String(certificateData), TimeSpan.FromDays(daysRemaining));
            }
        }

        private static X509Certificate2 CreateCertificate(byte[] certificateData, string? password)
        {
            try
            {
                return string.IsNullOrWhiteSpace(password)
                    ? new X509Certificate2(certificateData)
                    : new X509Certificate2(certificateData, password);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Failed to create certificate: {e.Message}");
                return null!;
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
            var activity = Activity.Current;
            if (activity != null)
            {
                activity.SetCustomProperty("SecurityContext", blocksContext);
            }
        }
    }
}
