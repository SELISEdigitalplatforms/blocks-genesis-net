using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Blocks.Genesis;

/// <summary>
/// Limits each authenticated subject (user, or OAuth client without a user) per service on
/// authorized and protected endpoints. Public endpoints, unauthenticated requests and principals
/// without <c>user_id</c> or <c>client_id</c> pass through without touching Redis.
/// </summary>
/// <remarks>
/// Registered by <see cref="ApplicationConfigurations.ConfigureApiBranchMiddleware"/> right after
/// <c>UseAuthorization()</c>. Rejections are written directly (no exception, no log entry).
/// </remarks>
public sealed class UserRateLimitMiddleware
{
    public const string RateLimitPolicyHeader = "RateLimit-Policy";
    public const string RateLimitHeader = "RateLimit";
    public const string RejectionTitle = "Rate Limit Exceeded";
    internal const string GrpcContentType = "application/grpc";
    internal const string GrpcResourceExhausted = "8";
    internal const string GrpcRejectionMessage = "Rate limit exceeded";

    private readonly RequestDelegate _next;

    public UserRateLimitMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!IsLimitedEndpoint(context.GetEndpoint()) ||
            !RateLimitSubject.TryResolve(context.User, out var subject))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var limiter = context.RequestServices.GetService<IUserRateLimiter>();
        var settings = context.RequestServices.GetService<UserRateLimitSettings>();
        if (limiter is null || settings is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var decision = await limiter
            .TryAcquireAsync(subject.BuildKey(settings.ServiceName), context.RequestAborted)
            .ConfigureAwait(false);

        if (decision is null)
        {
            // Fail open without headers: the limiter could not decide.
            await _next(context).ConfigureAwait(false);
            return;
        }

        var policy = FormatPolicy(subject.Type, settings.PermitLimit, settings.WindowSeconds);

        if (decision.Value.Allowed)
        {
            context.Response.Headers[RateLimitPolicyHeader] = policy;
            context.Response.Headers[RateLimitHeader] = FormatRateLimit(subject.Type, decision.Value.Remaining, decision.Value.ResetSeconds);
            await _next(context).ConfigureAwait(false);
            return;
        }

        await WriteRejectionAsync(context, subject.Type, policy, decision.Value.ResetSeconds).ConfigureAwait(false);
    }

    /// <summary>
    /// Same public-endpoint rule as JWT parsing: limited only when the endpoint has authorization
    /// metadata and no <see cref="IAllowAnonymous"/>.
    /// </summary>
    internal static bool IsLimitedEndpoint(Endpoint? endpoint)
    {
        if (endpoint is null)
        {
            return false;
        }

        return endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null &&
               endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null;
    }

    internal static string FormatPolicy(string subjectType, int limit, int windowSeconds) =>
        string.Create(CultureInfo.InvariantCulture, $"\"{subjectType}\";q={limit};w={windowSeconds}");

    internal static string FormatRateLimit(string subjectType, int remaining, int resetSeconds) =>
        string.Create(CultureInfo.InvariantCulture, $"\"{subjectType}\";r={remaining};t={resetSeconds}");

    internal static bool IsGrpcRequest(HttpRequest request) =>
        request.ContentType?.StartsWith(GrpcContentType, StringComparison.OrdinalIgnoreCase) == true;

    private static async Task WriteRejectionAsync(HttpContext context, string subjectType, string policy, int resetSeconds)
    {
        var retryAfter = BlocksConstants.RateLimitRetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        var response = context.Response;

        response.Headers[RateLimitPolicyHeader] = policy;
        response.Headers[RateLimitHeader] = FormatRateLimit(subjectType, 0, resetSeconds);

        if (IsGrpcRequest(context.Request))
        {
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = GrpcContentType;

            if (response.SupportsTrailers())
            {
                response.AppendTrailer("grpc-status", GrpcResourceExhausted);
                response.AppendTrailer("grpc-message", GrpcRejectionMessage);
                response.AppendTrailer("retry-after", retryAfter);
            }
            else
            {
                // Trailers-only form: gRPC clients read the status from the headers.
                response.Headers["grpc-status"] = GrpcResourceExhausted;
                response.Headers["grpc-message"] = GrpcRejectionMessage;
                response.Headers["retry-after"] = retryAfter;
            }

            return;
        }

        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.ContentType = "application/problem+json";
        response.Headers.RetryAfter = retryAfter;

        var problem = new ProblemDetails
        {
            Type = "https://httpstatuses.com/429",
            Title = RejectionTitle,
            Status = StatusCodes.Status429TooManyRequests,
            Detail = string.Create(CultureInfo.InvariantCulture, $"Rate limit exceeded. Retry after {BlocksConstants.RateLimitRetryAfterSeconds} seconds."),
            Instance = context.TraceIdentifier
        };

        await response.WriteAsync(JsonSerializer.Serialize(problem), Encoding.UTF8, context.RequestAborted).ConfigureAwait(false);
    }
}
