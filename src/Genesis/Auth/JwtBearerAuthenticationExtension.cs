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
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = false,
                        ValidateAudience = false,
                        ValidateIssuerSigningKey = false,
                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.Zero
                    };

                    options.Events = new JwtBearerEvents
                    {
                        OnTokenValidated = async context =>
                        {
                            var token = context.SecurityToken as JwtSecurityToken;
                            if (token == null)
                            {
                                context.Fail("Invalid token format");
                                return;
                            }

                            try
                            {
                                var claimsIdentity = context.Principal.Identity as ClaimsIdentity;
                                TokenHelper.HandleTokenIssuer(claimsIdentity, context.Request.Path.Value, token.RawData);
                                BlocksContext.CreateFromClaimsIdentity(claimsIdentity);
                                var bc = BlocksContext.GetContext();

                                var cert = await GetCertificateAsync(bc.TenantId, tenants, cacheDb);
                                if (cert == null)
                                {
                                    context.Fail("Certificate not found");
                                    return;
                                }

                                // Validate the token using the obtained certificate
                                var tokenHandler = new JwtSecurityTokenHandler();
                                tokenHandler.ValidateToken(token.RawData, new TokenValidationParameters
                                {
                                    IssuerSigningKey = new X509SecurityKey(cert),
                                    ValidateIssuerSigningKey = true,
                                    ValidateIssuer = true,
                                    ValidIssuer = tenants.GetTenantTokenValidationParameter(bc.TenantId)?.Issuer,
                                    ValidAudiences = tenants.GetTenantTokenValidationParameter(bc.TenantId)?.Audiences,
                                    ValidateAudience = true
                                }, out _);
                            }
                            catch (Exception ex)
                            {
                                context.Fail(ex.Message);
                            }
                        },
                        OnMessageReceived = context =>
                        {
                            context.Token = TokenHelper.GetToken(context.Request);
                            return Task.CompletedTask;
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
            var cachedPublicKey = await cacheDb.StringGetAsync(cacheKey);

            if (cachedPublicKey.HasValue)
            {
                // Load from cache
                return new X509Certificate2(Encoding.UTF8.GetBytes(cachedPublicKey));
            }

            // Fallback to loading certificate by path
            var tokenParameters = tenants.GetTenantTokenValidationParameter(tenantId);
            if (tokenParameters == null || string.IsNullOrWhiteSpace(tokenParameters.PublicCertificatePath))
            {
                throw new SecurityTokenException($"Token parameters for tenant {tenantId} not found");
            }

            var cert = CreateSecurityKey(tokenParameters.PublicCertificatePath, tokenParameters.PublicCertificatePassword);
            if (cert != null)
            {
                // Cache the public key for future use
                await cacheDb.StringSetAsync(cacheKey, cert.GetPublicKeyString());
            }

            return cert;
        }

        private static X509Certificate2 CreateSecurityKey(string signingKeyPath, string signingKeyPassword)
        {
            try
            {
                return string.IsNullOrWhiteSpace(signingKeyPassword)
                    ? new X509Certificate2(signingKeyPath)
                    : new X509Certificate2(signingKeyPath, signingKeyPassword);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error creating security key: {e.Message}");
                return null;
            }
        }



    }
}
