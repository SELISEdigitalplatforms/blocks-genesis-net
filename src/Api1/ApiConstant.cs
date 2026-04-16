namespace ApiOne
{
    using Blocks.Genesis;

    namespace DomainService.Utilities
    {
        public static class ApiConstant
        {
         

            private const string DefaultProvider = "azure";
            private const string RabbitMqProvider = "rabbitmq";


            public static MessageConfiguration GetMessageConfiguration(string messageConnectionString)
            {
                var provider = GetProvider(messageConnectionString);

                return provider switch
                {
                    RabbitMqProvider => CreateRabbitMqConfiguration(),
                    _ => CreateAzureServiceBusConfiguration()
                };
            }

            private static string GetProvider(string messageConnectionString)
            {
                if (Uri.TryCreate(messageConnectionString, UriKind.Absolute, out var uri))
                {
                    if (uri.Scheme.Equals("amqp", StringComparison.OrdinalIgnoreCase) ||
                        uri.Scheme.Equals("amqps", StringComparison.OrdinalIgnoreCase))
                    {
                        return RabbitMqProvider;
                    }
                }

                return DefaultProvider;
            }

            private static MessageConfiguration CreateRabbitMqConfiguration()
            {
                return new MessageConfiguration
                {
                    RabbitMqConfiguration = new RabbitMqConfiguration
                    {
                        ConsumerSubscriptions = [],
                    }
                };
            }

            private static MessageConfiguration CreateAzureServiceBusConfiguration()
            {
                return new MessageConfiguration
                {
                    AzureServiceBusConfiguration = new AzureServiceBusConfiguration
                    {
                        Queues = [],
                        Topics = []
                    }
                };
            }
        }
    }
}
