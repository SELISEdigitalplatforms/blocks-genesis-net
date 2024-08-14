using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Blocks.Genesis
{
    internal static class JwtBearerAuthenticationExtension
    {
        public static void JwtBearerAuthentication(this IServiceCollection services)
        {

            var securityContext = new SecurityContext();
            services.AddSingleton(typeof(ISecurityContext), securityContext);
            services.AddSingleton<IJwtValidationService, JwtValidationService>();

            var serviceProvider = services.BuildServiceProvider();
            var jwtValidationService = serviceProvider.GetRequiredService<IJwtValidationService>();
            

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
                                var tenantId = TokenHelper.GetBlocksSecret(context.Request);
                                if (string.IsNullOrWhiteSpace(tenantId))
                                {
                                    context.Fail("Blocks secret is missing.");
                                    return;
                                }

                                var origin = TokenHelper.GetOriginOrReferer(context.Request);

                                if (!string.IsNullOrWhiteSpace(origin))
                                {
                                    var isAllowed = token.Audiences.Any(x => x == origin);

                                    if (!isAllowed)
                                    {
                                        context.Fail("Invalid origin");
                                        return;
                                    }
                                }

                                try
                                {
                                    var claimsIdentity = context.Principal.Identity as ClaimsIdentity;
                                    var requestUri = context.Request.Path.Value;
                                    var jwtBearerToken = token.RawData;

                                    TokenHelper.HandleTokenIssuer(claimsIdentity, requestUri, jwtBearerToken);

                                    securityContext = SecurityContext.CreateFromClaimsIdentity(claimsIdentity);

                                    // Custom validation logic for tenant-specific parameters
                                    var tokenParameters = jwtValidationService.GetTokenParameters(tenantId);
                                    var signingKey = new X509SecurityKey(JwtValidationService.CreateSecurityKey(tokenParameters.SigningKeyPath, tokenParameters.SigningKeyPassword));
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
    }
}
