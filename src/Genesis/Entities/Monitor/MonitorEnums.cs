// Moved from blocks-monitor server/Alert.DomainService/Monitor/Entity/MonitorConfiguration.cs (dev, 2026-10-19).
// Same names and values; no behaviour change.
namespace Blocks.Genesis.Entities.Monitor;

public enum MonitorTypes
{
    HTTP,
    PING,
    TCP,
    DNS,
    KEYWORD
}

public enum ProtocolTypes
{
    HTTP,
    HTTPS
}

public enum HttpMethodTypes
{
    HEAD,
    GET,
    POST
}

public enum AuthorizationTypes
{
    NONE,
    BASIC,
    BEARER,
    API_KEY
}

public enum MonitorConfigurationTypes
{
    OutboundPing, // You send pings to other services
    InboundPing   // You receive pings from other services
}

public enum MonitorSourceTypes
{
    Infrastructure,
    DeployedServices,
    BlocksServices,
    ExternalServices,
    OtherServices,
}
