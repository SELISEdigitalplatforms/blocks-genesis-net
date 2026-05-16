using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using OpenTelemetry;
using System.Diagnostics;
using System.Text.Json;
using System.Collections.Generic;
using System.Net;

namespace Blocks.Genesis
{
    public class TenantValidationMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ITenants _tenants;
        private readonly ICryptoService _cryptoService;
        private readonly HashSet<string> _tenantValidationPrefixes;

        public TenantValidationMiddleware(RequestDelegate next, ITenants tenants, ICryptoService cryptoService, string[] tenantValidationPrefixes)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
            _cryptoService = cryptoService ?? throw new ArgumentNullException(nameof(cryptoService));
            _tenantValidationPrefixes = BuildTenantValidationPrefixes(tenantValidationPrefixes);
        }

        public async Task InvokeAsync(HttpContext context)
        {
            BlocksHttpContextAccessor.EnsureInitialized(context);
            var activity = Activity.Current;

            var endpoint = context.GetEndpoint();
            if (endpoint is null || (endpoint.DisplayName?.Contains("Controller") == false && endpoint.DisplayName?.Contains("GraphQL") == false))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            if (!RequiresTenantValidation(context.Request.Path))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            activity?.SetTag("http.headers", JsonSerializer.Serialize(SanitizeDictionary(context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()))));
            activity?.SetTag("http.query", JsonSerializer.Serialize(SanitizeDictionary(context.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString()))));
            var tenantId = await TenantContextHelper.ResolveTenantIdAsync(context.Request).ConfigureAwait(false);

            Tenant? tenant = null;

            if (string.IsNullOrWhiteSpace(tenantId))
            {
                if (IsLocalhostHost(context.Request.Host.Host))
                {
                    await RejectRequest(context, StatusCodes.Status400BadRequest, "BadRequest: Missing_Tenant_Key_Or_Id").ConfigureAwait(false);
                    return;
                }

                var baseUrl = NormalizeDomain(context.Request.Host.Host);

                tenant = _tenants.GetTenantByApplicationDomain(baseUrl);

                if (tenant is null)
                {
                    await RejectRequest(context, StatusCodes.Status404NotFound, "Not_Found: Application_Not_Found").ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                tenant = _tenants.GetTenantByID(tenantId);
            }

            if (tenant is null || tenant.IsDisabled)
            {
                await RejectRequest(context, StatusCodes.Status404NotFound, "Not_Found: Application_Not_Found").ConfigureAwait(false);
                return;
            }


            var origin = context.Request.Headers.Origin.FirstOrDefault();
            var referer = context.Request.Headers.Referer.FirstOrDefault();

            if (!IsValidOriginOrReferer(origin, referer, tenant))
            {
                await RejectRequest(context, StatusCodes.Status406NotAcceptable, "NotAcceptable: Invalid_Origin_Or_Referer").ConfigureAwait(false);
                return;
            }

            AttachTenantDataToActivity(tenant, origin, referer);
            TenantContextHelper.EnsureTenantContext(context, tenant.TenantId);

            if (context.Request.ContentType == "application/grpc" && context.Request.Headers.TryGetValue(BlocksConstants.BlocksGrpcKey, out var grpcKey))
            {
                var hash = _cryptoService.Hash(tenant.TenantId, tenant?.TenantSalt);
                if (hash != grpcKey)
                {
                    await RejectRequest(context, StatusCodes.Status403Forbidden, "Forbidden: Missing_Blocks_Service_Key").ConfigureAwait(false);
                    return;
                }
            }

            var requestSize = context.Request.ContentLength ?? 0;

            var originalBodyStream = context.Response.Body;
            var countingStream = new CountingWriteStream(originalBodyStream);
            context.Response.Body = countingStream;

            try
            {
                await _next(context).ConfigureAwait(false);

                var responseSize = countingStream.BytesWritten;

                activity?.SetTag("response.status.code", context.Response.StatusCode);
                activity?.SetTag("response.headers", JsonSerializer.Serialize(SanitizeDictionary(context.Response.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()))));
                activity?.SetTag("request.size.bytes", requestSize);
                activity?.SetTag("response.size.bytes", responseSize);
                activity?.SetTag("throughput.total.bytes", requestSize + responseSize);
                activity?.SetTag("usage", true);
            }
            finally
            {
                context.Response.Body = originalBodyStream;
                BlocksContext.ClearContext();
            }
        }

        private sealed class CountingWriteStream : Stream
        {
            private readonly Stream _innerStream;

            public CountingWriteStream(Stream innerStream)
            {
                _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
            }

            public long BytesWritten { get; private set; }

            public override bool CanRead => _innerStream.CanRead;

            public override bool CanSeek => _innerStream.CanSeek;

            public override bool CanWrite => _innerStream.CanWrite;

            public override long Length => _innerStream.Length;

            public override long Position
            {
                get => _innerStream.Position;
                set => _innerStream.Position = value;
            }

            public override void Flush()
            {
                _innerStream.Flush();
            }

            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                return _innerStream.FlushAsync(cancellationToken);
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return _innerStream.Read(buffer, offset, count);
            }

            public override int Read(Span<byte> buffer)
            {
                return _innerStream.Read(buffer);
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                return _innerStream.ReadAsync(buffer, cancellationToken);
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return _innerStream.ReadAsync(buffer, offset, count, cancellationToken);
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                return _innerStream.Seek(offset, origin);
            }

            public override void SetLength(long value)
            {
                _innerStream.SetLength(value);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                BytesWritten += count;
                _innerStream.Write(buffer, offset, count);
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                BytesWritten += buffer.Length;
                _innerStream.Write(buffer);
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                BytesWritten += buffer.Length;
                return _innerStream.WriteAsync(buffer, cancellationToken);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                BytesWritten += count;
                return _innerStream.WriteAsync(buffer, offset, count, cancellationToken);
            }

            protected override void Dispose(bool disposing)
            {
                // The ASP.NET response stream is owned by the host and must remain open.
            }

            public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private static bool IsValidOriginOrReferer(string? origin, string? referer, Tenant tenant)
        {
            // Allow local development origins in tenant middleware.
            if (IsLocalhostHeader(origin) || IsLocalhostHeader(referer))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(origin) && string.IsNullOrWhiteSpace(referer))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(origin) && !IsDomainAllowed(origin, tenant))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(referer) && !IsDomainAllowed(referer, tenant))
            {
                return false;
            }

            return true;
        }

        private static bool IsLocalhostHeader(string? headerValue)
        {
            if (string.IsNullOrWhiteSpace(headerValue))
            {
                return false;
            }

            if (!Uri.TryCreate(headerValue, UriKind.Absolute, out var uri))
            {
                return false;
            }

            return IsLocalhostHost(uri.Host);
        }

        private static bool IsDomainAllowed(string? headerValue, Tenant tenant)
        {
            if (string.IsNullOrWhiteSpace(headerValue)) return true;

            try
            {
                var uri = new Uri(headerValue);
                var host = NormalizeDomain(uri.Host);

                var allowedDomains = tenant.Applications?
                    .Select(a => NormalizeDomain(a.Domain))
                    .Where(d => !string.IsNullOrWhiteSpace(d))
                    ?? [];

                return allowedDomains.Contains(host, StringComparer.OrdinalIgnoreCase);
            }
            catch (UriFormatException)
            {
                return false; // Invalid header format
            }
        }

        private static string NormalizeDomain(string domain) => BlocksContext.NormalizeDomain(domain);

        private static bool IsLocalhostHost(string? host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return IPAddress.TryParse(host, out var ipAddress) && IPAddress.IsLoopback(ipAddress);
        }

        private static Task RejectRequest(HttpContext context, int statusCode, string message)
        {
            context.Response.StatusCode = statusCode;
            return context.Response.WriteAsync(JsonSerializer.Serialize(new BaseResponse
            {
                IsSuccess = false,
                Errors = new Dictionary<string, string> { { "Message", message } }
            }));
        }

        private static string ResolveApplicationDomain(Tenant tenant, string? origin, string? referer)
        {
            var browserHost = string.Empty;

            if (!string.IsNullOrWhiteSpace(origin) && !IsLocalhostHeader(origin))
            {
                if (Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
                    browserHost = NormalizeDomain(originUri.Host);
            }
            else if (!string.IsNullOrWhiteSpace(referer) && !IsLocalhostHeader(referer))
            {
                if (Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
                    browserHost = NormalizeDomain(refererUri.Host);
            }

            // Browser call: return the exact matching domain from Applications
            if (!string.IsNullOrWhiteSpace(browserHost))
            {
                var match = tenant.Applications?
                    .FirstOrDefault(a => NormalizeDomain(a.Domain).Equals(browserHost, StringComparison.OrdinalIgnoreCase));

                if (match != null) return match.Domain;
            }

            // Non-browser call
            return string.Empty;
        }

        private static void AttachTenantDataToActivity(Tenant tenant, string? origin, string? referer)
        {
            var applicationDomain = ResolveApplicationDomain(tenant, origin, referer);

            var securityData = BlocksContext.Create(
                tenant.TenantId,
                Array.Empty<string>(),
                string.Empty,
                false,
                string.Empty,
                string.Empty,
                DateTime.MinValue,
                string.Empty,
                Array.Empty<string>(),
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                tenant.TenantId,
                applicationDomain
            );

            BlocksContext.SetContext(securityData, false);

            Baggage.SetBaggage("TenantId", tenant.TenantId);
            Baggage.SetBaggage("IsFromCloud", tenant.IsRootTenant.ToString());

            var current = Activity.Current;
            if (current != null)
            {
                current.SetTag("SecurityContext", JsonSerializer.Serialize(securityData));
                current.SetTag("ApplicationDomain", applicationDomain);
            }
        }

        private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "authorization",
            "cookie",
            "set-cookie",
            "secret",
            "x-blocks-service-key",
            "x-api-key",
            "token",
            "access_token",
            "refresh_token",
            "password"
        };

        private static Dictionary<string, string> SanitizeDictionary(Dictionary<string, string> source)
        {
            var sanitized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in source)
            {
                sanitized[entry.Key] = IsSensitiveKey(entry.Key) ? "[REDACTED]" : entry.Value;
            }

            return sanitized;
        }

        private static bool IsSensitiveKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            return SensitiveKeys.Contains(key)
                   || key.Contains("token", StringComparison.OrdinalIgnoreCase)
                   || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
                   || key.Contains("password", StringComparison.OrdinalIgnoreCase);
        }

        private static HashSet<string> BuildTenantValidationPrefixes(IEnumerable<string>? configuredPrefixes)
        {
            var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "api"
            };

            if (configuredPrefixes != null)
            {
                foreach (var prefix in configuredPrefixes)
                {
                    AddPrefix(prefixes, prefix);
                }
            }

            return prefixes;
        }

        private bool RequiresTenantValidation(PathString requestPath)
        {
            var normalizedPath = requestPath.Value?.Trim('/') ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return false;
            }

            foreach (var prefix in _tenantValidationPrefixes)
            {
                if (normalizedPath.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                    || normalizedPath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void AddPrefix(HashSet<string> target, string? rawPrefix)
        {
            if (string.IsNullOrWhiteSpace(rawPrefix))
            {
                return;
            }

            var trimmed = rawPrefix.Trim().Trim('/');
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                target.Add(trimmed);
            }
        }

    }
}
