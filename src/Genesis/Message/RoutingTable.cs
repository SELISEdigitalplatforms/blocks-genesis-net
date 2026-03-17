using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Blocks.Genesis
{
    public record RoutingTable
    {
        public IDictionary<string, RoutingInfo> Routes { get; } = new SortedDictionary<string, RoutingInfo>();

        public RoutingTable(IServiceCollection serviceCollection)
        {
            BuildRoutingTable(serviceCollection);
        }

        private void BuildRoutingTable(IServiceCollection serviceCollection)
        {
            foreach (var serviceDescriptor in serviceCollection)
            {
                if (serviceDescriptor.ImplementationType == null)
                {
                    continue;
                }

                var consumerMethods = GetConsumerMethods(serviceDescriptor.ImplementationType);


                foreach (var consumerMethod in consumerMethods)
                {
                    var messageType = consumerMethod.GetParameters()[0].ParameterType;

                    var routingInfo = new RoutingInfo(
                        messageType.Name,
                        messageType,
                        serviceDescriptor.ServiceType,
                        consumerMethod
                    );

                    if (Routes.ContainsKey(routingInfo.ContextName))
                    {
                        throw new InvalidOperationException($"Message of type {routingInfo.ContextType} is being used by {Routes[routingInfo.ContextName].ConsumerType}");
                    }

                    Routes.Add(routingInfo.ContextName, routingInfo);
                }
            }
        }

        private static IEnumerable<MethodInfo> GetConsumerMethods(Type implementorType)
        {
            return implementorType
                .GetTypeInfo()
                .ImplementedInterfaces
                .Where(interfaceType => interfaceType.IsGenericType && interfaceType.GetGenericTypeDefinition() == typeof(IConsumer<>))
                .SelectMany(interfaceType => implementorType.GetMethods()
                    .Where(method =>
                        method.Name.Equals("Consume") &&
                        method.GetParameters().Length == 1));
        }
    }
}
