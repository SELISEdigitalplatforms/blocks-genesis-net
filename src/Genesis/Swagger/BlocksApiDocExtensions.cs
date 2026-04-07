using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.Reflection;

namespace Blocks.Genesis
{
    /// <summary>
    /// Extension methods for registering and configuring Swagger/OpenAPI documentation
    /// with Blocks platform conventions including service path prefixing and security schemes.
    /// </summary>
    public static class BlocksApiDocExtensions
    {
        private const string BearerSchemeDescription = "Enter 'Bearer' [space] and then your valid token";
        private const string BlocksKeyDescription = "API key needed to access the endpoints.";

        /// <summary>
        /// Registers Swagger generation with Blocks-specific configuration including
        /// JWT bearer auth, custom header security, and optional service path prefixing.
        /// </summary>
        /// <param name="services">The service collection to register Swagger into.</param>
        /// <param name="blocksSwaggerOptions">Blocks Swagger configuration options.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="blocksSwaggerOptions"/> is null.</exception>
        public static void AddBlocksSwagger(this IServiceCollection services, BlocksSwaggerOptions blocksSwaggerOptions)
        {
            ArgumentNullException.ThrowIfNull(blocksSwaggerOptions);

            services.AddSwaggerGen(options =>
            {
                var openApiInfo = new OpenApiInfo
                {
                    Version = blocksSwaggerOptions.Version,
                    Title = blocksSwaggerOptions.Title,
                    Description = blocksSwaggerOptions.Description
                };

                options.SwaggerDoc(blocksSwaggerOptions.Version, openApiInfo);

                var xmlFileName = string.IsNullOrWhiteSpace(blocksSwaggerOptions.XmlCommentsFilePath)
                    ? $"{Assembly.GetExecutingAssembly().GetName().Name}.xml"
                    : blocksSwaggerOptions.XmlCommentsFilePath;

                options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, xmlFileName));

                EnableBearerAuthorization(options, blocksSwaggerOptions.EnableBearerAuth);
                AddCustomHeader(options, BlocksConstants.BlocksKey, BlocksKeyDescription);

                if (!string.IsNullOrWhiteSpace(blocksSwaggerOptions.ServiceName))
                {
                    options.DocumentFilter<AddServiceVersionToPathsFilter>(blocksSwaggerOptions);
                }
            });
        }

        /// <summary>
        /// Adds JWT Bearer security definition and requirement to Swagger options.
        /// </summary>
        private static void EnableBearerAuthorization(SwaggerGenOptions options, bool isEnabled)
        {
            if (!isEnabled)
            {
                return;
            }

            var securityScheme = new OpenApiSecurityScheme
            {
                Name = "JWT Authentication",
                Description = BearerSchemeDescription,
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
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
                { securityScheme, Array.Empty<string>() }
            });
        }

        /// <summary>
        /// Adds a custom API key header security definition and requirement to Swagger options.
        /// </summary>
        private static void AddCustomHeader(SwaggerGenOptions options, string headerName, string description)
        {
            var securityScheme = new OpenApiSecurityScheme
            {
                Name = headerName,
                Description = description,
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.ApiKey,
                Scheme = headerName,
                Reference = new OpenApiReference
                {
                    Id = headerName,
                    Type = ReferenceType.SecurityScheme
                }
            };

            options.AddSecurityDefinition(headerName, securityScheme);
            options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                { securityScheme, Array.Empty<string>() }
            });
        }
    }
}
