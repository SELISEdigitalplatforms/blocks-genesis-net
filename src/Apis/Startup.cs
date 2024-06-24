using Blocks.Genesis;
using MassTransit;
using MongoDB.Driver;
using StackExchange.Redis;

namespace Api1
{
    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers();
            services.AddHttpClient();

            services.AddSingleton<IMongoClient, MongoClient>(sp => new MongoClient("mongodb://localhost:27017"));

            //services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect("10.30.65.4:6379,abortConnect=false,connectTimeout=50000,syncTimeout=50000"));

            services.AddMassTransit(x =>
            {
                x.UsingRabbitMq((context, cfg) =>
                {
                    cfg.Host(new Uri("rabbitmq://10.30.65.4:5672/"), h =>
                    {
                        h.Username("test");
                        h.Password("test");
                    });
                });
            });


            ApplicationConfigurations.ConfigureServices(services, "Service-API-Test_Two");


        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            ApplicationConfigurations.ConfigureTraceContextMiddleware(app);

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });
        }
    }

}
