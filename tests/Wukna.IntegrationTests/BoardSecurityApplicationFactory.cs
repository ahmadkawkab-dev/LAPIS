namespace Wukna.IntegrationTests;

using System.Collections.Concurrent;
using System.Data.Common;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Wukna.Features.Realtime;
using Wukna.Features.Users;
using Wukna.Shared.Data.AppDbContext;
using Xunit;

internal sealed class BoardSecurityApplicationFactory : WuknaWebApplicationFactory
{
    private readonly PostgresFixture postgres;
    private readonly IInterceptor[] interceptors;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "wukna-board-security-" + Guid.NewGuid().ToString("N"));
    public Action<IServiceCollection>? ConfigureOverrides { get; init; }
    public BoardSecurityApplicationFactory(PostgresFixture postgres, ManualTimeProvider clock, params IInterceptor[] interceptors) : base(postgres, clock)
    {
        this.postgres = postgres; this.interceptors = interceptors; UseKestrel(0);
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment(Environments.Production);
        builder.UseSetting("Authentication:Google:FrontendBaseUrl", "https://wukna.test");
        builder.UseSetting("ReverseProxy:KnownProxyIp", "127.0.0.1");
        builder.UseSetting("DataProtection:KeysPath", Path.Combine(directory, "keys"));
        builder.UseSetting("ProfileImages:Directory", Path.Combine(directory, "images"));
        builder.ConfigureServices(services =>
        {
            // Drive cleanup explicitly and isolate foreground SQL counts from timers.
            // Authentication, endpoints, Kestrel and the native lifetime manager remain real.
            foreach (var worker in services.Where(service => service.ServiceType == typeof(IHostedService) &&
                (service.ImplementationType?.Assembly == typeof(Program).Assembly ||
                 service.ImplementationFactory?.Method.DeclaringType?.Assembly == typeof(Program).Assembly)).ToArray())
                services.Remove(worker);
            services.RemoveAll<WuknaDbContext>(); services.RemoveAll<DbContextOptions<WuknaDbContext>>();
            services.AddDbContext<WuknaDbContext>(options => options.UseNpgsql(postgres.ConnectionString)
                .UseSnakeCaseNamingConvention().AddInterceptors(interceptors));
            ConfigureOverrides?.Invoke(services);
        });
    }
    public HttpClient Client(User user)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.BaseAddress = new Uri(Assert.Single(Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ChatControlTestSupport.Token(user));
        Assert.Equal(Environments.Production, Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
        return client;
    }
    public HubConnection Connection(HttpClient http, User user) => new HubConnectionBuilder().WithUrl(new Uri(http.BaseAddress!, BoardHub.Path), options =>
    {
        options.Transports = HttpTransportType.WebSockets;
        options.Headers["X-Forwarded-Proto"] = "https";
        options.AccessTokenProvider = () => Task.FromResult<string?>(ChatControlTestSupport.Token(user));
    }).Build();
    public override async ValueTask DisposeAsync()
    {
        try { await base.DisposeAsync(); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

internal sealed class BoardSocketObservation
{
    private const string FenceEvent = "SecurityDeliveryFence";
    private readonly ConcurrentDictionary<string, TaskCompletionSource> fences = new();
    private readonly HubConnection connection;
    public ConcurrentQueue<string> Titles { get; } = new();
    public ConcurrentQueue<Guid> Revocations { get; } = new();
    public ConcurrentQueue<Guid> SummaryRemovals { get; } = new();
    public BoardSocketObservation(HubConnection connection)
    {
        this.connection = connection;
        connection.On<BoardUpdatedEvent>(BoardRealtimeEvents.BoardUpdated, item => Titles.Enqueue(item.Title));
        connection.On<BoardAccessRevokedEvent>(BoardRealtimeEvents.BoardAccessRevoked, item => Revocations.Enqueue(item.BoardId));
        connection.On<BoardSummaryRemovedEvent>(BoardRealtimeEvents.BoardSummaryRemoved, item => SummaryRemovals.Enqueue(item.BoardId));
        connection.On<string>(FenceEvent, id => fences[id].TrySetResult());
    }
    public async Task FenceAsync(BoardSecurityApplicationFactory factory, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fences[id] = completion;
        await factory.Services.GetRequiredService<IHubContext<BoardHub>>().Clients.Client(connection.ConnectionId!).SendAsync(FenceEvent, id, ct);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        fences.TryRemove(id, out _);
    }
}

internal sealed class BoardSqlCounter : DbCommandInterceptor
{
    private int count;
    public int Count => Volatile.Read(ref count);
    public void Reset() => Interlocked.Exchange(ref count, 0);
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
        InterceptionResult<DbDataReader> result, CancellationToken ct = default) { Interlocked.Increment(ref count); return ValueTask.FromResult(result); }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
        InterceptionResult<int> result, CancellationToken ct = default) { Interlocked.Increment(ref count); return ValueTask.FromResult(result); }
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData data,
        InterceptionResult<object> result, CancellationToken ct = default) { Interlocked.Increment(ref count); return ValueTask.FromResult(result); }
}
