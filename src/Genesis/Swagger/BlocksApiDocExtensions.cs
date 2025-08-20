using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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

                options.SwaggerDoc(blocksSwaggerOptions.Version, openApiInfo);

                var xmlFilename = string.IsNullOrWhiteSpace(blocksSwaggerOptions.XmlCommentsFilePath)
                    ? $"{Assembly.GetExecutingAssembly().GetName().Name}.xml"
                    : blocksSwaggerOptions.XmlCommentsFilePath;

                options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, xmlFilename));

                EnableAuthorization(options, blocksSwaggerOptions.EnableBearerAuth);
                AddCustomHeader(options, BlocksConstants.BlocksKey, "API key needed to access the endpoints.");

                if(!string.IsNullOrEmpty(blocksSwaggerOptions.ServiceName))
                     options.DocumentFilter<AddServiceVersionToPathsFilter>(blocksSwaggerOptions);
            });

        }

        private static void EnableAuthorization(SwaggerGenOptions options, bool isEnable)
        {
            if (!isEnable) return;

            var securityScheme = new OpenApiSecurityScheme
            {
                Name = "JWT Authentication",
                Description = "Enter 'Bearer' [space] and then your valid token",
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
