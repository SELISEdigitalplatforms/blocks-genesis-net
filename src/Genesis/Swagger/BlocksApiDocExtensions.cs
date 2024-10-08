using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.Reflection;

namespace Blocks.Genesis
{
    public static class BlocksApiDocExtensions
    {
        public static void AddBlocksSwagger(this IServiceCollection services, BlocksSwaggerOptions blocksSwaggerOptions)
        {
            if (blocksSwaggerOptions == null) return;

            services.AddSwaggerGen(options =>
            {
                var openApiInfo = new OpenApiInfo
                {
                    Version = blocksSwaggerOptions.Version,
                    Title = blocksSwaggerOptions.Title,
                    Description = blocksSwaggerOptions.Description
                };

                if (!string.IsNullOrWhiteSpace(blocksSwaggerOptions.TermsOfServiceUrl))
                {
                    openApiInfo.TermsOfService = new Uri(blocksSwaggerOptions.TermsOfServiceUrl);
                }

                if (blocksSwaggerOptions.Contact != null)
                {
                    openApiInfo.Contact = new OpenApiContact
                    {
                        Name = blocksSwaggerOptions.Contact.Name,
                        Email = blocksSwaggerOptions.Contact.Email,
                        Url = string.IsNullOrWhiteSpace(blocksSwaggerOptions.Contact.Url) ? new Uri("/") : new Uri(blocksSwaggerOptions.Contact.Url)
                    };
                }

                if (blocksSwaggerOptions.Contact != null)
                {
                    openApiInfo.License = new OpenApiLicense
                    {
                        Name = blocksSwaggerOptions.License.Name,
                        Url = string.IsNullOrWhiteSpace(blocksSwaggerOptions.License.Url) ? new Uri("/") : new Uri(blocksSwaggerOptions.License.Url)
                    };
                }

                options.SwaggerDoc(blocksSwaggerOptions.Version, openApiInfo);

                // Enable XML comments for more detailed documentation
                var xmlFilename = string.IsNullOrWhiteSpace(blocksSwaggerOptions.XmlCommentsFilePath) ? $"{Assembly.GetExecutingAssembly().GetName().Name}.xml" : blocksSwaggerOptions.XmlCommentsFilePath;
                options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, xmlFilename));

                // Add support for JWT Bearer token authentication
                EnableAuthorization(options, blocksSwaggerOptions.EnableBearerAuth);

            });
        }

        private static void EnableAuthorization(SwaggerGenOptions options, bool isEnable)
        {
            if (!isEnable) return;

            var securityScheme = new OpenApiSecurityScheme
            {
                Name = "JWT Authentication",
                Description = "Enter 'Bearer' [space] and then your valid token in the text input below.\n\nExample: 'Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9'",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.Http,
                Scheme = "bearer", // must be lower case
                BearerFormat = "JWT",
                Reference = new OpenApiReference
                {
                    Id = JwtBearerDefaults.AuthenticationScheme,
                    Type = ReferenceType.SecurityScheme
                }
            };
            options.AddSecurityDefinition(securityScheme.Reference.Id, securityScheme);
            options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = "Bearer"
                        }
                    },
                    Array.Empty<string>()
                }
            });
        }

    }
}
