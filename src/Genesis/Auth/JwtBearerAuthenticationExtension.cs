using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;

namespace Blocks.Genesis
{
    internal static class JwtBearerAuthenticationExtension
    {
        public static void JwtBearerAuthentication(this IServiceCollection services)
        {

            var serviceProvider = services.BuildServiceProvider();
            var tenants = serviceProvider.GetRequiredService<ITenants>();

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

                            if (token != null)
                            {
                                var isKeyExist = context.Request.Headers.TryGetValue(BlocksConstants.BlocksKey, out StringValues tenantId);

                                if (!isKeyExist)
                                {
                                    throw new SecurityTokenException("Missing 'X-Blocks-Key' header");
                                }

                                try
                                {
                                    var claimsIdentity = context.Principal.Identity as ClaimsIdentity;
                                    var requestUri = context.Request.Path.Value;
                                    var jwtBearerToken = token.RawData;

                                    TokenHelper.HandleTokenIssuer(claimsIdentity, requestUri, jwtBearerToken);

                                    SecurityContext.CreateFromClaimsIdentity(claimsIdentity);

                                    // Custom validation logic for tenant-specific parameters
                                    var tokenParameters = tenants.GetTenantTokenValidationParameter(tenantId);

                                    if (tokenParameters == null || string.IsNullOrWhiteSpace(tokenParameters.SigningKeyPath))
                                    {
                                        throw new SecurityTokenException($"Token parameter for {tenantId} is not found");
                                    }

                                    var signingKey = new X509SecurityKey(CreateSecurityKey(tokenParameters.SigningKeyPath, tokenParameters.SigningKeyPassword));
                                    var tokenHandler = new JwtSecurityTokenHandler();

                                    try
                                    {
                                        tokenHandler.ValidateToken(token.RawData, new TokenValidationParameters
                                        {
                                            IssuerSigningKey = signingKey,
                                            ValidateIssuerSigningKey = true,
                                            ValidateIssuer = true,
                                            ValidIssuer = tokenParameters.Issuer,
                                            ValidAudiences = tokenParameters.Audiences,
                                            ValidateAudience = true
                                        }, out _);
                                    }
                                    catch (SecurityTokenException)
                                    {
                                        context.Fail("Invalid token");
                                        return;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    context.Fail(ex.Message);
                                }
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

        private static X509Certificate2 CreateSecurityKey(string signingKeyPath, string signingKeyPassword)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(signingKeyPassword))
                {
                    return new X509Certificate2(signingKeyPath);
                }
                else
                {
                    return new X509Certificate2(signingKeyPath, signingKeyPassword);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error creating security key: {e.Message}");
                return null;
            }
        }
    }
}
