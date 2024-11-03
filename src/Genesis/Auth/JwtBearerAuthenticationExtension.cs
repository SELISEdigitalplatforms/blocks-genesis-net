using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;

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

                            context.Options.TokenValidationParameters = new TokenValidationParameters
                            {
                                ValidateLifetime = true,
                                ClockSkew = TimeSpan.Zero,
                                IssuerSigningKey = new X509SecurityKey(certificate),
                                ValidateIssuerSigningKey = true,
                                ValidateIssuer = true,
                                ValidIssuer = tenants.GetTenantTokenValidationParameter(bc.TenantId)?.Issuer,
                                ValidAudiences = tenants.GetTenantTokenValidationParameter(bc.TenantId)?.Audiences,
                                ValidateAudience = true,
                                SaveSigninToken = true,
                            };
                        },
                        OnTokenValidated = async context =>
                        {
                            var claimsIdentity = context.Principal.Identity as ClaimsIdentity;
                            TokenHelper.HandleTokenIssuer(claimsIdentity, context.Request.Path.Value);
                            BlocksContext.CreateFromClaimsIdentity(claimsIdentity);
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

            services.AddAuthorization();
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
            //if (certificate != null)
            //{
            //    var expirationDays = tokenParameters.CertificateValidForNumberOfDays - (DateTime.UtcNow - tokenParameters.IssueDate).Days - 1;
            //    await cacheDb.StringSetAsync(cacheKey, certificate, TimeSpan.FromDays(expirationDays));
            //}
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



    }
}
