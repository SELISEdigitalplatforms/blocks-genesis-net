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

            var serviceProvider = services.BuildServiceProvider();
            var jwtValidationService = serviceProvider.GetRequiredService<IJwtValidationService>();


            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateIssuerSigningKey = true,
                        ValidateLifetime = true,
                        ValidIssuers = jwtValidationService.GetIssuers(),
                        ValidAudiences = jwtValidationService.GetAudiences(),
                        IssuerSigningKeys = jwtValidationService.GetSecurityKeys(),
                        ClockSkew = TimeSpan.Zero
                    };
                    options.Events = new JwtBearerEvents
                    {
                        OnTokenValidated = async context =>
                        {
                            var token = context.SecurityToken as JwtSecurityToken;
                            if (token != null)
                            {
                                var origin = TokenHelper.GetHostOfRequestOrigin(context.Request);

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
