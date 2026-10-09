namespace Wukna.IntegrationTests;

using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wukna.Features.Auth;
using Wukna.Features.Users;
using Wukna.Shared.Data.AppDbContext;
using Wukna.Shared.Errors;
using Xunit;

// Runs Program's unchanged pipeline over a real TCP connection. Only test services,
// credentials and storage locations differ; no failure endpoints or middleware are added.
internal sealed class ProductionKestrelApplicationFactory : WuknaWebApplicationFactory
{
    private readonly PostgresFixture postgres;
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"wukna-http-contracts-{Guid.NewGuid():N}");
    private readonly ConcurrentDictionary<string, HttpFailureProbe> probes = new();
    private readonly HttpFailureLogs logs = new();

    public ProductionKestrelApplicationFactory(PostgresFixture postgres)
        : base(postgres, new ManualTimeProvider(DateTimeOffset.UtcNow))
    {
        this.postgres = postgres;
        UseKestrel(0);
    }

    public HttpFailureProbe Inject(HttpFailureMode mode)
    {
        var probe = new HttpFailureProbe(mode);
        probes[probe.Id] = probe;
        return probe;
    }

    public HttpClient CreateHttpClient(User? user = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        client.DefaultRequestVersion = HttpVersion.Version11;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        client.Timeout = TimeSpan.FromSeconds(30);
        // Exercise the trusted proxy's HTTPS indication without weakening secure cookies.
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        Assert.Equal(Environments.Production, Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
        var server = Services.GetRequiredService<IServer>();
        Assert.Contains("Kestrel", server.GetType().FullName!);
        // Explicit client options otherwise retain their default http://localhost base URI.
        client.BaseAddress = new Uri(Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses));
        if (user is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                Services.GetRequiredService<JwtTokenGenerator>().CreateAccessToken(user).Token);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment(Environments.Production);
        builder.UseSetting("Authentication:Google:FrontendBaseUrl", "https://wukna.test");
        builder.UseSetting("ReverseProxy:KnownProxyIp", "127.0.0.1");
        builder.UseSetting("DataProtection:KeysPath", Path.Combine(directory, "keys"));
        builder.UseSetting("ProfileImages:Directory", Path.Combine(directory, "images"));
        builder.ConfigureLogging(logging => logging.AddProvider(logs));
        builder.ConfigureServices(services =>
        {
            services.AddHttpContextAccessor();
            services.RemoveAll<WuknaDbContext>();
            services.RemoveAll<DbContextOptions<WuknaDbContext>>();
            services.AddDbContext<WuknaDbContext>((provider, options) => options
                .UseNpgsql(postgres.ConnectionString)
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(new HttpFailureInterceptor(
                    provider.GetRequiredService<IHttpContextAccessor>(), probes, logs)));
        });
    }

    public override async ValueTask DisposeAsync()
    {
        try { await base.DisposeAsync(); }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class HttpFailureInterceptor(
        IHttpContextAccessor accessor,
        ConcurrentDictionary<string, HttpFailureProbe> probes,
        HttpFailureLogs logs) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            var context = accessor.HttpContext;
            if (context is null || context.Request.Path != "/api/profile" ||
                !probes.TryGetValue(context.Request.Headers[HttpFailureProbe.Header].ToString(), out var probe))
                return result;

            probe.RequestId = context.TraceIdentifier;
            probe.TraceId = Activity.Current?.TraceId.ToString();
            probe.SpanId = Activity.Current?.SpanId.ToString();
            probe.DatabaseToken = cancellationToken;
            probe.RequestToken = context.RequestAborted;
            logs.Track(probe);
            probe.Entered.TrySetResult();

            if (probe.Mode == HttpFailureMode.Cancel)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException exception) when (context.RequestAborted.IsCancellationRequested)
                {
                    probe.Exception = exception;
                    probe.CancellationObserved.TrySetResult();
                    throw;
                }
            }

            if (probe.Mode == HttpFailureMode.AfterResponseStarted)
            {
                context.Response.ContentType = "text/plain";
                // A deliberately incomplete entity makes the transport abort observable.
                context.Response.ContentLength = HttpFailureProbe.Prefix.Length + 1;
                await context.Response.WriteAsync(HttpFailureProbe.Prefix, cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
                probe.ResponseStarted = context.Response.HasStarted;
                probe.Started.TrySetResult();
                await probe.Release.Task.WaitAsync(cancellationToken);
            }

            throw probe.Exception;
        }
    }

    private sealed class HttpFailureLogs : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();
        private readonly ConcurrentDictionary<string, HttpFailureProbe> tracked = new();

        public void Track(HttpFailureProbe probe) => tracked[probe.Id] = probe;
        public void SetScopeProvider(IExternalScopeProvider provider) => scopes = provider;
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);
        public void Dispose() { }

        private sealed class CaptureLogger(HttpFailureLogs owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner.scopes.Push(state);
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel) || exception is null ||
                    (category != "Microsoft.AspNetCore.Server.Kestrel" &&
                     category != typeof(UnexpectedExceptionHandler).FullName))
                    return;
                var values = new Dictionary<string, string>();
                owner.scopes.ForEachScope((scope, captured) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> properties)
                        foreach (var property in properties) captured[property.Key] = property.Value?.ToString() ?? "";
                }, values);
                var properties = state is IEnumerable<KeyValuePair<string, object?>> entries
                    ? entries.ToDictionary(entry => entry.Key, entry => entry.Value?.ToString() ?? "")
                    : new Dictionary<string, string>();
                foreach (var probe in owner.tracked.Values)
                    if (ReferenceEquals(exception, probe.Exception))
                    {
                        var log = new HttpFailureLog(category, eventId.Id, exception, values, properties);
                        probe.FailureLogs.Enqueue(log);
                        if (category == "Microsoft.AspNetCore.Server.Kestrel")
                            probe.ErrorLog.TrySetResult(log);
                        else
                            probe.HandledErrorLog.TrySetResult(log);
                    }
            }
        }
    }
}

internal enum HttpFailureMode { Unhandled, Cancel, AfterResponseStarted, DependencyCancellation }

internal sealed class HttpFailureProbe(HttpFailureMode mode)
{
    public const string Header = "X-Wukna-Test-Failure";
    public const string Prefix = "partial-response";
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public HttpFailureMode Mode { get; } = mode;
    public Exception Exception { get; set; } = mode == HttpFailureMode.DependencyCancellation
        ? new OperationCanceledException("test-only-sensitive-failure-marker")
        : new InvalidOperationException("test-only-sensitive-failure-marker");
    public string? RequestId { get; set; }
    public string? TraceId { get; set; }
    public string? SpanId { get; set; }
    public CancellationToken DatabaseToken { get; set; }
    public CancellationToken RequestToken { get; set; }
    public bool ResponseStarted { get; set; }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<HttpFailureLog> ErrorLog { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<HttpFailureLog> HandledErrorLog { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentQueue<HttpFailureLog> FailureLogs { get; } = new();
}

internal sealed record HttpFailureLog(string Category, int EventId, Exception Exception,
    IReadOnlyDictionary<string, string> Scopes, IReadOnlyDictionary<string, string> Properties);
