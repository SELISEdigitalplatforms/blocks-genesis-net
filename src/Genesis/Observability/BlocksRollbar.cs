using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Rollbar;

// Rollbar ships its own ILogger; this file always means the framework one.
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Blocks.Genesis;

/// <summary>
/// Error reporting to Rollbar, alongside the Genesis log pipeline rather than replacing it.
/// </summary>
/// <remarks>
/// Opt-in: with no <c>Rollbar:AccessToken</c> in configuration every member here is a no-op, so a
/// service that has not been seeded stays silent instead of failing at startup. The token is
/// expected to arrive with the rest of a service's deploy-time configuration.
/// <para>
/// Serilog remains the log store of record (console, Mongo, LMT). Rollbar exists for alerting and
/// grouping, so it only ever receives faults -- see <see cref="GlobalExceptionHandlerMiddleware"/>,
/// which reports exactly the exceptions it has already classified as server-side (5xx). Nothing
/// mapping to a 4xx is sent: a validation failure or a 404 is a business outcome, and paying
/// per-occurrence for those is waste.
/// </para>
/// </remarks>
public static class BlocksRollbar
{
    private const string AccessTokenKey = "Rollbar:AccessToken";
    private const string EnvironmentKey = "Rollbar:Environment";
    private const string CodeVersionKey = "Rollbar:CodeVersion";
    private const string ExtraScrubFieldsKey = "Rollbar:ExtraScrubFields";

    /// <summary>
    /// Header and payload names never sent to Rollbar. Rollbar scrubs a default set already; these
    /// are the ones specific to this platform, and getting them wrong would ship tenant
    /// credentials to a third party. They live here, once, for exactly that reason.
    /// </summary>
    private static readonly string[] DefaultScrubFields =
    [
        "x-blocks-key",
        "X-Blocks-Key",
        "Authorization",
        "authorization",
        "access_token",
        "refresh_token",
        "accessToken",
        "refreshToken",
        "password",
        "clientSecret",
        "client_secret",
        "connectionString",
        "secretValue",
        "OAuthToken",
    ];

    private static string _serviceName = "unknown";
    private static string _environmentName = "unknown";

    /// <summary>
    /// True once <see cref="Initialize"/> has found a token and brought Rollbar up. Nothing may
    /// touch <see cref="RollbarLocator"/> before this is true: it throws when the infrastructure
    /// was never initialised.
    /// </summary>
    public static bool IsEnabled { get; private set; }

    /// <summary>
    /// Brings up the Rollbar singleton. Safe to call when unconfigured (does nothing) and safe to
    /// call twice (the second call is ignored), since the underlying infrastructure throws on
    /// re-initialisation.
    /// </summary>
    /// <param name="configuration">Where the <c>Rollbar:*</c> values are read from.</param>
    /// <param name="serviceName">Reported as <c>service</c>, so an item names its origin.</param>
    /// <param name="fallbackEnvironment">Used when <c>Rollbar:Environment</c> is not set.</param>
    public static void Initialize(IConfiguration configuration, string serviceName, string fallbackEnvironment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (IsEnabled)
        {
            return;
        }

        var accessToken = configuration[AccessTokenKey];
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return;
        }

        _serviceName = string.IsNullOrWhiteSpace(serviceName) ? "unknown" : serviceName;
        _environmentName = configuration[EnvironmentKey] is { Length: > 0 } configured
            ? configured
            : fallbackEnvironment;

        var config = new RollbarInfrastructureConfig(accessToken, _environmentName);

        var extraScrubFields = configuration.GetSection(ExtraScrubFieldsKey).Get<string[]>() ?? [];

        config.RollbarLoggerConfig.RollbarDataSecurityOptions.Reconfigure(
            new RollbarDataSecurityOptions(
                PersonDataCollectionPolicies.None,
                IpAddressCollectionPolicy.CollectAnonymized,
                [.. DefaultScrubFields, .. extraScrubFields],
                []));

        var codeVersion = configuration[CodeVersionKey];
        if (!string.IsNullOrWhiteSpace(codeVersion))
        {
            config.RollbarLoggerConfig.RollbarPayloadAdditionOptions.CodeVersion = codeVersion;
        }

        RollbarInfrastructure.Instance.Init(config);
        IsEnabled = true;
    }

    /// <summary>
    /// Says in the log whether reporting is on, and surfaces Rollbar's own delivery failures.
    /// </summary>
    /// <remarks>
    /// Without this, a token of the wrong scope, a suspended token and a blocked egress route all
    /// look identical from the outside: no items and no explanation. Rollbar transmits on a
    /// background queue and swallows its own failures by design, so its internal event stream is
    /// the only place those surface.
    /// </remarks>
    /// <param name="logger">Where the status line and delivery failures are written.</param>
    public static void AttachDiagnostics(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (!IsEnabled)
        {
            logger.LogInformation("Rollbar reporting is OFF: no {ConfigKey} configured.", AccessTokenKey);
            return;
        }

        logger.LogInformation(
            "Rollbar reporting is ON for {Service} in environment {RollbarEnvironment}.",
            _serviceName,
            _environmentName);

        RollbarInfrastructure.Instance.QueueController!.InternalEvent += (_, args) =>
        {
            switch (args)
            {
                // Rollbar answered and refused the payload. A token of the wrong scope lands here.
                case RollbarApiErrorEventArgs apiError:
                    logger.LogError(
                        "Rollbar rejected a payload: {ErrorCode} {ErrorDescription}",
                        apiError.ErrorCode,
                        apiError.ErrorDescription);
                    break;

                case PayloadDropEventArgs dropped:
                    logger.LogWarning("Rollbar dropped a payload: {Reason}", dropped.Reason);
                    break;

                case CommunicationErrorEventArgs commsError:
                    logger.LogWarning(
                        commsError.Error,
                        "Rollbar transmission failed, {RetriesLeft} retries left.",
                        commsError.RetriesLeft);
                    break;

                case InternalErrorEventArgs internalError:
                    logger.LogWarning(
                        internalError.Error,
                        "Rollbar internal error: {Details}",
                        internalError.Details);
                    break;

                // Proof of delivery, at Debug so a healthy service stays quiet.
                case CommunicationEventArgs:
                    logger.LogDebug("Rollbar accepted a payload.");
                    break;

                default:
                    break;
            }
        };
    }

    /// <summary>
    /// Reports a fault, enriched with the tenant and user it happened to.
    /// </summary>
    /// <remarks>
    /// Called by <see cref="GlobalExceptionHandlerMiddleware"/> for unhandled server-side faults. A
    /// service that turns an exception into a 5xx response itself, in an MVC exception filter say,
    /// never reaches that middleware and should call this directly; nothing else will report it.
    /// <para>
    /// Never throws. Telemetry must not turn a handled fault into a second, worse one.
    /// </para>
    /// </remarks>
    /// <param name="exception">The fault to report.</param>
    /// <param name="context">The request it happened on, when there is one.</param>
    /// <param name="statusCode">The status the caller will receive.</param>
    public static void Report(Exception exception, HttpContext? context, int statusCode)
    {
        if (!IsEnabled || exception is null)
        {
            return;
        }

        try
        {
            RollbarLocator.RollbarInstance.Error(BuildPackage(exception, context, statusCode));
        }
        catch
        {
            // Swallowed deliberately: the exception is already on its way to the Genesis log
            // pipeline, so a Rollbar outage costs us nothing but this one report.
        }
    }

    private static CustomKeyValuePackageDecorator BuildPackage(Exception exception, HttpContext? context, int statusCode)
    {
        var blocksContext = BlocksContext.GetContext();

        var custom = new Dictionary<string, object?>
        {
            // `service` names the app and `component` which half of it, as two fields rather than
            // one "blocks-os-api" string, so either can be filtered without parsing.
            ["service"] = _serviceName,
            ["component"] = "api",
            ["statusCode"] = statusCode,
            ["tenantId"] = blocksContext?.TenantId,
            ["organizationId"] = blocksContext?.OrganizationId,
            ["method"] = context?.Request.Method,
            ["path"] = context?.Request.Path.Value,
            ["traceId"] = context?.TraceIdentifier,
        };

        // Rollbar renders the custom block verbatim, so blank entries are pure noise in the UI.
        var populated = custom
            .Where(entry => entry.Value is string text ? text.Length > 0 : entry.Value is not null)
            .ToDictionary(entry => entry.Key, entry => entry.Value);

        IRollbarPackage package = new ExceptionPackage(exception, exception.Message);

        var userId = blocksContext?.UserId;
        if (!string.IsNullOrWhiteSpace(userId))
        {
            package = new PersonPackageDecorator(
                package,
                new Rollbar.DTOs.Person(userId) { Email = blocksContext?.Email });
        }

        return new CustomKeyValuePackageDecorator(package, populated);
    }
}
