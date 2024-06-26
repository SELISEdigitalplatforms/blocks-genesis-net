using System.Reflection;

namespace Blocks.Genesis
{
    internal record RoutingInfo
    {
        public string ContextName { get; }
        public Type ContextType { get; }
        public Type ConsumerType { get; }
        public MethodInfo ConsumerMethod { get; }

        public RoutingInfo(string contextName, Type contextType, Type consumerType, MethodInfo consumerMethod)
        {
            ContextName = contextName;
            ContextType = contextType;
            ConsumerType = consumerType;
            ConsumerMethod = consumerMethod;
        }
    }
}
