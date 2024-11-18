using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using System.Diagnostics;
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
                            var validationParameter = tenants.GetTenantTokenValidationParameter(bc.TenantId);
                            context.Options.TokenValidationParameters = new TokenValidationParameters
                            {
                                ValidateLifetime = true,
                                ClockSkew = TimeSpan.Zero,
                                IssuerSigningKey = new X509SecurityKey(certificate),
                                ValidateIssuerSigningKey = true,
                                ValidateIssuer = true,
                                ValidIssuer = validationParameter?.Issuer,
                                ValidAudiences = validationParameter?.Audiences,
                                ValidateAudience = true,
                                SaveSigninToken = true,
                            };
                        },
                        OnTokenValidated = async context =>
                        {
                            var claimsIdentity = context.Principal.Identity as ClaimsIdentity;

                            StoreBlocksContextInActivity(BlocksContext.CreateFromClaimsIdentity(claimsIdentity));
                        },
                        OnAuthenticationFailed = authenticationFailedContext =>
                        {
                            Console.WriteLine("Authentication failed: " + authenticationFailedContext.Exception.Message);
                            return Task.CompletedTask;
                        },
                        OnForbidden = forbiddenContext =>
                        {
                            Console.WriteLine("Authorization failed: Forbidden");
                            return Task.CompletedTask;
                        }
                    };
                });

            services.AddAuthorization(options =>
            {
                options.AddPolicy("Protected", policy =>
                            policy.Requirements.Add(new ProtectedEndpointAccessRequirement()));
            });

            services.AddScoped<IAuthorizationHandler, ProtectedEndpointAccessHandler>();
        }

        private static async Task<X509Certificate2> GetCertificateAsync(string tenantId, ITenants tenants, IDatabase cacheDb)
        {
            var cacheKey = $"{BlocksConstants.TenantTokenPublicCertificateCachePrefix}{tenantId}";
            var cachedCertificate = await cacheDb.StringGetAsync(cacheKey);
            var tokenParameters = tenants.GetTenantTokenValidationParameter(tenantId);

            if (cachedCertificate.HasValue)
            {
                return CreateSecurityKey(cachedCertificate, tokenParameters.PublicCertificatePassword);
            }

            if (tokenParameters == null || string.IsNullOrWhiteSpace(tokenParameters.PublicCertificatePath))
            {
                throw new SecurityTokenException($"Token parameters for tenant {tenantId} not found");
            }

            var certificate = await GetPublicCertificateAsync(tokenParameters.PublicCertificatePath);
            if (certificate != null)
            {
                var expirationDays = tokenParameters.CertificateValidForNumberOfDays - (DateTime.UtcNow - tokenParameters.IssueDate).Days - 1;
                await cacheDb.StringSetAsync(cacheKey, certificate, TimeSpan.FromDays(expirationDays));
            }
            return CreateSecurityKey(certificate, tokenParameters.PublicCertificatePassword);
        }

        private static async Task<byte[]?> GetPublicCertificateAsync(string signingKeyPath)
        {
            try
            {
                byte[] certificateData;

                if (Uri.IsWellFormedUriString(signingKeyPath, UriKind.Absolute))
                {
                    using var httpClient = new HttpClient();
                    certificateData = await httpClient.GetByteArrayAsync(signingKeyPath);
                }
                else
                {
                    certificateData = File.ReadAllBytes(signingKeyPath);
                }

                return certificateData;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error creating security key: {e.Message}");
                return null;
            }
        }

        private static X509Certificate2 CreateSecurityKey(byte[] signingKey, string signingKeyPassword)
        {
            try
            {
                if (signingKey == null) throw new ArgumentNullException();

                return string.IsNullOrWhiteSpace(signingKeyPassword)
                    ? new X509Certificate2(signingKey)
                    : new X509Certificate2(signingKey, signingKeyPassword);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error creating security key: {e.Message}");
                return null;
            }
        }

        private static void StoreBlocksContextInActivity(BlocksContext bc)
        {
            var activity = Activity.Current;
            if (activity != null)
            {
                var securityData = BlocksContext.CreateFromTuple((bc.TenantId, bc.Roles, bc.UserId, bc.IsAuthenticated, bc.RequestUri, bc.OrganizationId, bc.ExpireOn, bc.Email, bc.Permissions, bc.TenantId));

                activity.SetCustomProperty("SecurityContext", JsonSerializer.Serialize(securityData));
            }
        }


    }
}
