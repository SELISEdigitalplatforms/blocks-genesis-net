using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using OpenTelemetry;
using System.Diagnostics;
using System.Text.Json;

namespace Blocks.Genesis
{
    public class TenantValidationMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ITenants _tenants;
        private readonly ICryptoService _cryptoService;

        public TenantValidationMiddleware(RequestDelegate next, ITenants tenants, ICryptoService cryptoService)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
            _cryptoService = cryptoService ?? throw new ArgumentNullException(nameof(cryptoService));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var activity = Activity.Current;

            var endpoint = context.GetEndpoint();
            if (endpoint is null || endpoint.DisplayName?.Contains("Controller") == false)
            {
                Console.WriteLine("Skipping tenant validation for controller-action");
                await _next(context);
                return;
            }

            activity?.SetTag("http.headers", JsonSerializer.Serialize(context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString())));
            activity?.SetTag("http.query", JsonSerializer.Serialize(context.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString())));
            var tenantId = TenantContextHelper.ResolveTenantId(context.Request);

            Tenant? tenant = null;

            if (string.IsNullOrWhiteSpace(tenantId))
            {
                var baseUrl = context.Request.Host.Value;

                tenant = _tenants.GetTenantByApplicationDomain(baseUrl);

                if (tenant is null)
                {
                    await RejectRequest(context, StatusCodes.Status404NotFound, "Not_Found: Application_Not_Found");
                    return;
                }
            }
            else
            {
                tenant = _tenants.GetTenantByID(tenantId);
            }

            if (tenant is null || tenant.IsDisabled)
            {
                await RejectRequest(context, StatusCodes.Status404NotFound, "Not_Found: Application_Not_Found");
                return;
            }


            if (!IsValidOriginOrReferer(context, tenant))
            {
                await RejectRequest(context, StatusCodes.Status406NotAcceptable, "NotAcceptable: Invalid_Origin_Or_Referer");
                return;
            }

            AttachTenantDataToActivity(tenant);
            TenantContextHelper.EnsureTenantContext(context, tenant.TenantId);

            if (context.Request.ContentType == "application/grpc" && context.Request.Headers.TryGetValue(BlocksConstants.BlocksGrpcKey, out var grpcKey))
            {
                var hash = _cryptoService.Hash(tenant.TenantId, tenant.TenantSalt);
                if (hash != grpcKey)
                {
                    await RejectRequest(context, StatusCodes.Status403Forbidden, "Forbidden: Missing_Blocks_Service_Key");
                    return;
                }
            }

            var requestSize = context.Request.ContentLength ?? 0;

            var originalBodyStream = context.Response.Body;
            var countingStream = new CountingWriteStream(originalBodyStream);
            context.Response.Body = countingStream;

            try
            {
                await _next(context);

                var responseSize = countingStream.BytesWritten;

                activity?.SetTag("response.status.code", context.Response.StatusCode);
                activity?.SetTag("response.headers", JsonSerializer.Serialize(context.Response.Headers.ToDictionary(h => h.Key, h => h.Value.ToString())));
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

            public override async ValueTask DisposeAsync()
            {
                await Task.CompletedTask;
            }
        }

        private static bool IsValidOriginOrReferer(HttpContext context, Tenant tenant)
        {
            var originHeader = context.Request.Headers.Origin.FirstOrDefault();
            var refererHeader = context.Request.Headers.Referer.FirstOrDefault();

            return IsDomainAllowed(originHeader, tenant) || IsDomainAllowed(refererHeader, tenant);
        }

        private static bool IsDomainAllowed(string? headerValue, Tenant tenant)
        {
            if (string.IsNullOrWhiteSpace(headerValue)) return true;

            try
            {
                var uri = new Uri(headerValue);
                var host = uri.Host;

                var normalizedApplicationDomain = NormalizeDomain(tenant.ApplicationDomain);
                var allowedDomains = tenant.AllowedDomains?.Select(NormalizeDomain) ?? [];

                return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                       host.Equals(normalizedApplicationDomain, StringComparison.OrdinalIgnoreCase) ||
                       allowedDomains.Contains(host, StringComparer.OrdinalIgnoreCase);
            }
            catch (UriFormatException)
            {
                return false; // Invalid header format
            }
        }

        private static string NormalizeDomain(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain)) return string.Empty;

            return domain.Replace("http://", "")
                 .Replace("https://", "")
                 .Split(":")[0]
                 .Trim();
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

        private static void AttachTenantDataToActivity(Tenant tenant)
        {
            var securityData = BlocksContext.Create(
                tenant.TenantId,
                Array.Empty<string>(),
                string.Empty,
                false,
                tenant.ApplicationDomain,
                string.Empty,
                DateTime.MinValue,
                string.Empty,
                Array.Empty<string>(),
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                tenant.TenantId
            );

            BlocksContext.SetContext(securityData, false);

            Baggage.SetBaggage("TenantId", tenant.TenantId);
            Baggage.SetBaggage("IsFromCloud", tenant.IsRootTenant.ToString());

            var current = Activity.Current;
            if (current != null)
            {
                current.SetTag("SecurityContext", JsonSerializer.Serialize(securityData));
                current.SetTag("ApplicationDomain", tenant.ApplicationDomain);
            }
        }


    }
}
