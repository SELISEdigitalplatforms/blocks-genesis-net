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
            services.AddSingleton<IJwtValidationService, JwtValidationService>();

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
                                var jwtValidationService = context.HttpContext.RequestServices.GetRequiredService<IJwtValidationService>();
                                var issuer = token.Issuer;
                                var audienceId = token.Audiences.FirstOrDefault();

                                var validationParameters = await jwtValidationService.GetValidationParametersAsync(issuer, audienceId);

                                if (validationParameters == null)
                                {
                                    context.Fail("Invalid token parameters");
                                    return;
                                }

                                var origin = TokenRetrievalHelper.GetHostOfRequestOrigin(context.Request);

                                if (!string.IsNullOrWhiteSpace(origin))
                                {
                                    var isAllowed = validationParameters.Audiences.Any(x => x == origin);

                                    if (!isAllowed)
                                    {
                                        context.Fail("Invalid origin");
                                        return;
                                    }
                                }

                                var tokenValidationParameters = new TokenValidationParameters
                                {
                                    ValidateIssuer = true,
                                    ValidIssuer = validationParameters.Issuer,
                                    ValidateAudience = true,
                                    ValidAudiences = validationParameters.Audiences,
                                    ValidateIssuerSigningKey = true,
                                    IssuerSigningKey = new X509SecurityKey(TokenRetrievalHelper.CreateSecurityKey(validationParameters))
                                };

                                try
                                {
                                    var principal = new JwtSecurityTokenHandler().ValidateToken(token.RawData, tokenValidationParameters, out _);
                                    context.Principal = principal;

                                    var claimsIdentity = context.Principal.Identity as ClaimsIdentity;
                                    var requestUri = context.Request.Path.Value;
                                    var jwtBearerToken = token.RawData;

                                    TokenRetrievalHelper.HandleTokenIssuer(claimsIdentity, requestUri, jwtBearerToken);
                                }
                                catch (Exception ex)
                                {
                                    context.Fail(ex.Message);
                                }
                            }
                        },
                        OnMessageReceived = context =>
                        {
                            context.Token = TokenRetrievalHelper.GetToken(context.Request);
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
