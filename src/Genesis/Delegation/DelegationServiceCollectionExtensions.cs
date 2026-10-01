using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocks.Genesis;

/// <summary>
/// Registers delegated access.
/// <para>
/// The delegated token is attached by <c>HttpService</c>, not by a message handler on the
/// <see cref="HttpClient"/> pipeline. Every outbound Blocks call goes through that service so it is
/// traced, which makes it the single place that sees them all — and it keeps the token off any call
/// that bypasses tracing.
/// </para>
/// <para>
/// The exchange itself uses a separate named client, so redeeming a grant never re-enters
/// <c>HttpService</c> and can never recurse into redeeming a grant.
/// </para>
/// </summary>
public static class DelegationServiceCollectionExtensions
{
    public static IServiceCollection AddBlocksDelegation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IDelegationGrantStore, DelegationGrantStore>();
        services.AddSingleton<IDelegationGrantFactory, DelegationGrantFactory>();
        services.AddSingleton<IDelegationTokenEndpointResolver, DelegationTokenEndpointResolver>();
        services.AddSingleton<IDelegatedTokenProvider, DelegatedTokenProvider>();

        // The client that redeems the grant. Kept separate from anything HttpService touches.
        services.AddHttpClient(DelegationConstants.ExchangeHttpClientName);

        services.AddHostedService<DelegationStartupValidator>();

        return services;
    }
}

/// <summary>
/// Fails the host at startup when neither <c>BLOCKS_IAM_BASE_URL</c> nor
/// <c>BLOCKS_IAM_TOKEN_ENDPOINT</c> is configured. Never silently defaults to a guessed path.
/// </summary>
internal sealed class DelegationStartupValidator : IHostedService
{
    private readonly IDelegationTokenEndpointResolver _resolver;
    private readonly ILogger<DelegationStartupValidator> _logger;

    public DelegationStartupValidator(IDelegationTokenEndpointResolver resolver, ILogger<DelegationStartupValidator> logger)
    {
        _resolver = resolver;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _resolver.EnsureConfigured();
        DelegationStartupLog.Configured(_logger);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static partial class DelegationStartupLog
{
    [LoggerMessage(EventId = 7040, Level = LogLevel.Information, Message = "Delegated access is configured. The IAM token endpoint resolves by OIDC discovery, per tenant, on first use.")]
    public static partial void Configured(ILogger logger);
}
