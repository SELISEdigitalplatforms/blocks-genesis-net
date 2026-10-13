using Blocks.Genesis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace XUnitTest.RateLimit;

/// <summary>
/// End-to-end: an in-process host runs the real ConfigureApiBranchMiddleware against a real Redis
/// (spec section 10). Identity comes from a test authentication handler.
/// </summary>
public class UserRateLimitEndToEndTests
{
    private const string AuthHeader = "X-Test-Auth";

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(AuthHeader, out var raw))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>();
            foreach (var part in raw.ToString().Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                if (pair.Length == 2)
                {
                    claims.Add(new Claim(pair[0], pair[1]));
                }
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
        }
    }

    internal sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private Host(WebApplication app, HttpClient client, Func<int> redisCalls, CapturingLogger logs)
        {
            _app = app;
            Client = client;
            RedisCalls = redisCalls;
            Logs = logs;
        }

        public HttpClient Client { get; }
        public Func<int> RedisCalls { get; }
        public CapturingLogger Logs { get; }

        public static async Task<Host> StartAsync(string serviceName, int limit = 20, bool relaxedTimeout = true)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            var logs = new CapturingLoggerProvider();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);

            var (cache, calls) = RedisTestServer.CountingClient();
            builder.Services.AddSingleton(cache);
            builder.Services.AddSingleton(new Mock<ITenants>().Object);
            builder.Services.AddSingleton(new Mock<ICryptoService>().Object);
            builder.Services.AddSingleton(new Mock<IBlocksSecret>().Object);
            builder.Services.AddRouting();
            builder.Services
                .AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            builder.Services.AddAuthorizationBuilder()
                .AddPolicy("Protected", p => p.RequireAuthenticatedUser());

            // The same registration ConfigureServices performs, with the limit taken from the
            // UserRateLimitPerSecond variable (spec fixture) and the given service name.
            builder.Services.AddUserRateLimiting(
                _ => serviceName,
                name => name == BlocksConstants.UserRateLimitEnvironmentVariable ? limit.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
            if (relaxedTimeout)
            {
                builder.Services.AddSingleton<IUserRateLimiter>(sp => new RedisSlidingWindowRateLimiter(
                    sp.GetRequiredService<ICacheClient>(),
                    sp.GetRequiredService<UserRateLimitSettings>(),
                    null,
                    RedisTestServer.Relaxed));
            }

            var app = builder.Build();
            app.UseRouting();
            ApplicationConfigurations.ConfigureApiBranchMiddleware(app);

            app.MapGet("/secure", () => Results.Ok("secure")).RequireAuthorization();
            app.MapGet("/protected", () => Results.Ok("protected")).WithMetadata(new ProtectedEndPointAttribute("res"));
            app.MapGet("/public", () => Results.Ok("public")).RequireAuthorization().AllowAnonymous();
            app.MapGet("/open", () => Results.Ok("open"));
            app.MapGet("/throws", (Func<IResult>)(() => throw new BlocksRateLimitException("integration limit"))).AllowAnonymous();
            app.MapPost("/grpc.Test/Secure", (HttpContext ctx) =>
            {
                ctx.Response.ContentType = "application/grpc";
                ctx.Response.AppendTrailer("grpc-status", "0");
                return Task.CompletedTask;
            }).RequireAuthorization();
            app.MapPost("/grpc.Test/Plain", (HttpContext ctx) =>
            {
                ctx.Response.ContentType = "application/grpc";
                ctx.Response.AppendTrailer("grpc-status", "0");
                return Task.CompletedTask;
            });

            await app.StartAsync();
            return new Host(app, app.GetTestClient(), calls, logs.Logger);
        }

        public Task<HttpResponseMessage> Get(string path, string? identity, Action<HttpRequestMessage>? configure = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (identity is not null)
            {
                request.Headers.Add(AuthHeader, identity);
            }

            configure?.Invoke(request);
            return Client.SendAsync(request);
        }

        public Task<HttpResponseMessage> Grpc(string method, string? identity, Action<HttpRequestMessage>? configure = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, method)
            {
                Content = new ByteArrayContent([0, 0, 0, 0, 0]),
                Version = HttpVersion.Version20
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc");
            if (identity is not null)
            {
                request.Headers.Add(AuthHeader, identity);
            }

            configure?.Invoke(request);
            return Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private const string U1T1 = "user_id=u1;tenant_id=t1";

    private static string NewService() => $"svc-a-{Guid.NewGuid():N}";

    private static async Task<HttpResponseMessage[]> Concurrent(Host host, string path, string identity, int count) =>
        await Task.WhenAll(Enumerable.Range(0, count).Select(_ => host.Get(path, identity)));

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : string.Empty;

    [Fact]
    public async Task Step2_TwentyOneConcurrent_Gives20Ok_And1RateLimited() // H4, H5, C15
    {
        var service = NewService();
        await using var host = await Host.StartAsync(service);

        var responses = await Concurrent(host, "/secure", U1T1, 21);

        var ok = responses.Where(r => r.StatusCode == HttpStatusCode.OK).ToList();
        var limited = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.Equal(20, ok.Count);
        Assert.All(ok, r =>
        {
            Assert.Equal("\"user\";q=20;w=1", Header(r, "RateLimit-Policy"));
            Assert.Matches("^\"user\";r=([0-9]|1[0-9]);t=1$", Header(r, "RateLimit"));
        });
        Assert.Equal("30", Header(limited, "Retry-After"));
        Assert.Equal("\"user\";q=20;w=1", Header(limited, "RateLimit-Policy"));
        Assert.Equal("\"user\";r=0;t=30", Header(limited, "RateLimit"));
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await limited.Content.ReadAsStringAsync());
        Assert.Equal("Rate Limit Exceeded", body.RootElement.GetProperty("title").GetString());
        Assert.Equal("Rate limit exceeded. Retry after 30 seconds.", body.RootElement.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("instance").GetString()));

        Assert.True(await RedisTestServer.Database.HashExistsAsync($"ratelimit:{service}:t1:user:u1", "cu"));
        // The rejection writes no log entry: no warning/error and no exception-handler line.
        Assert.DoesNotContain(host.Logs.Entries, e => e.Level >= LogLevel.Warning);
        Assert.DoesNotContain(host.Logs.Entries, e => e.Message.Contains("Unhandled exception") || e.Message.Contains("Rate limit exceeded", StringComparison.OrdinalIgnoreCase));
        await RedisTestServer.FlushAsync($"ratelimit:{service}:*");
    }

    [Fact]
    public async Task Step4_ProtectedEndpoint_ClientDualAndSeparateCounts() // H6, H7, H9
    {
        var service = NewService();
        await using var host = await Host.StartAsync(service, limit: 3);

        var protectedResponses = await Concurrent(host, "/protected", U1T1, 4);
        Assert.Equal(3, protectedResponses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, protectedResponses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));

        var client = await Concurrent(host, "/secure", "client_id=c9;tenant_id=t1", 4);
        Assert.Equal(3, client.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(client.Where(r => r.StatusCode == HttpStatusCode.OK), r => Assert.Equal("\"client\";q=3;w=1", Header(r, "RateLimit-Policy")));
        Assert.True(await RedisTestServer.Database.KeyExistsAsync($"ratelimit:{service}:t1:client:c9"));

        // Dual principal (user_id + client_id) counts under the user key, which is already in cooldown.
        var dual = await host.Get("/secure", "user_id=u1;client_id=c9x;tenant_id=t1");
        Assert.Equal(HttpStatusCode.TooManyRequests, dual.StatusCode);
        Assert.False(await RedisTestServer.Database.KeyExistsAsync($"ratelimit:{service}:t1:client:c9x"));

        Assert.Equal(HttpStatusCode.OK, (await host.Get("/secure", "user_id=u2;tenant_id=t1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Get("/secure", "user_id=u1;tenant_id=t2")).StatusCode);
        await RedisTestServer.FlushAsync($"ratelimit:{service}:*");
    }

    [Fact]
    public async Task Step5_CooldownOnOneService_DoesNotAffectAnother() // H8
    {
        var serviceA = NewService();
        var serviceB = NewService();
        await using var hostA = await Host.StartAsync(serviceA, limit: 2);
        await using var hostB = await Host.StartAsync(serviceB, limit: 2);

        await Concurrent(hostA, "/secure", U1T1, 3);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await hostA.Get("/secure", U1T1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await hostB.Get("/secure", U1T1)).StatusCode);
        await RedisTestServer.FlushAsync($"ratelimit:{serviceA}:*");
        await RedisTestServer.FlushAsync($"ratelimit:{serviceB}:*");
    }

    [Fact]
    public async Task Step6_PublicOpenUnauthenticatedAndSubjectless_SendNoRedisCommand() // C1, C2, C3
    {
        var service = NewService();
        await using var host = await Host.StartAsync(service, limit: 2);

        foreach (var path in new[] { "/public", "/open" })
        {
            foreach (var identity in new[] { null, U1T1 })
            {
                var responses = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => host.Get(path, identity)));
                Assert.All(responses, r =>
                {
                    Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                    Assert.False(r.Headers.Contains("RateLimit"));
                    Assert.False(r.Headers.Contains("RateLimit-Policy"));
                });
            }
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Get("/secure", null)).StatusCode);

        var neither = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => host.Get("/secure", "tenant_id=t1")));
        Assert.All(neither, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        Assert.Equal(0, host.RedisCalls());
    }

    [Fact]
    public async Task Step11_IdentityHeadersDoNotChangeTheKey() // C11
    {
        var service = NewService();
        await using var host = await Host.StartAsync(service);

        await host.Get("/secure", U1T1, r =>
        {
            r.Headers.Add("X-User-Id", "u2");
            r.Headers.Add("X-Forwarded-For", "10.1.1.1");
        });

        Assert.True(await RedisTestServer.Database.KeyExistsAsync($"ratelimit:{service}:t1:user:u1"));
        Assert.False(await RedisTestServer.Database.KeyExistsAsync($"ratelimit:{service}:t1:user:u2"));
        await RedisTestServer.FlushAsync($"ratelimit:{service}:*");
    }

    [Fact]
    public async Task Step12_GrpcAuthorizedMethod_IsResourceExhausted_PlainMethodIsNot() // H12, C17
    {
        var service = NewService();
        await using var host = await Host.StartAsync(service, limit: 2);

        HttpResponseMessage? last = null;
        for (var i = 0; i < 3; i++)
        {
            last = await host.Grpc("/grpc.Test/Secure", U1T1);
        }

        Assert.Equal(HttpStatusCode.OK, last!.StatusCode);
        await last.Content.ReadAsByteArrayAsync();
        var status = last.TrailingHeaders.TryGetValues("grpc-status", out var s) ? s.Single()
            : last.Headers.GetValues("grpc-status").Single();
        Assert.Equal("8", status);
        var message = last.TrailingHeaders.TryGetValues("grpc-message", out var m) ? m.Single() : last.Headers.GetValues("grpc-message").Single();
        Assert.Equal("Rate limit exceeded", message);
        var retry = last.TrailingHeaders.TryGetValues("retry-after", out var ra) ? ra.Single() : last.Headers.GetValues("retry-after").Single();
        Assert.Equal("30", retry);

        var plain = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            host.Grpc("/grpc.Test/Plain", null, r => r.Headers.Add(BlocksConstants.BlocksGrpcKey, "service-key"))));
        Assert.All(plain, r =>
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.False(r.Headers.Contains("RateLimit"));
        });
        await RedisTestServer.FlushAsync($"ratelimit:{service}:*");
    }

    [Fact]
    public async Task Step14_ConsumerThrownRateLimitException_StillMapsTo429() // C19
    {
        await using var host = await Host.StartAsync(NewService());

        var response = await host.Get("/throws", null);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Rate Limit Exceeded", body.RootElement.GetProperty("title").GetString());
        Assert.Equal("integration limit", body.RootElement.GetProperty("detail").GetString());
        Assert.False(response.Headers.Contains("RateLimit"));
    }

    [Fact]
    public async Task Step15_DefaultRegistration_EnforcesWithoutLimiterCode() // H15, startup log
    {
        var service = NewService();
        await using var host = await Host.StartAsync(service, limit: 2, relaxedTimeout: false);

        Assert.Contains(host.Logs.Entries, e => e.Level == LogLevel.Information && e.Message.Contains(service) && e.Message.Contains("Source=environment"));

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            statuses.Add((await host.Get("/secure", U1T1)).StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        await RedisTestServer.FlushAsync($"ratelimit:{service}:*");
    }
}
