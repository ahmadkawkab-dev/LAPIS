namespace Wukna.IntegrationTests;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wukna.Features.Auth;
using Wukna.Features.Board;
using Wukna.Features.Chat;
using Wukna.Features.Users;
using Wukna.Shared.Data.AppDbContext;
using Xunit;

internal static class ChatControlTestSupport
{
    internal sealed record SeedData(Board Board, User Owner, User Guest, User Peer, User Outsider, ManualTimeProvider Clock);
    internal static async Task<SeedData> Seed(PostgresFixture postgres, CancellationToken ct, bool guests = true)
    {
        await postgres.ResetAsync(ct);
        var clock = new ManualTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        User owner = User("owner"), guest = User("guest"), peer = User("peer"), outsider = User("outsider");
        var board = new Board { Title = "Chat controls", CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
        board.Memberships.Add(new() { User = owner, Role = BoardRole.Owner, CanEdit = true });
        if (guests)
        {
            board.Memberships.Add(new() { User = guest, Role = BoardRole.Guest });
            board.Memberships.Add(new() { User = peer, Role = BoardRole.Guest });
        }
        await using var db = postgres.CreateContext();
        db.Users.AddRange(owner, guest, peer, outsider); db.Boards.Add(board);
        await db.SaveChangesAsync(ct);
        return new(board, owner, guest, peer, outsider, clock);
    }
    internal static User User(string name) => new() { Email = name + "@chat-control.test", NormalizedEmail = name.ToUpperInvariant() + "@CHAT-CONTROL.TEST" };
    internal static string Path(SeedData seed) => $"/api/boards/{seed.Board.Id}/chat";
    internal static string Token(User user) => new JwtTokenGenerator(new JwtOptions {
        Issuer = WuknaWebApplicationFactory.JwtIssuer, Audience = WuknaWebApplicationFactory.JwtAudience,
        SigningKey = WuknaWebApplicationFactory.JwtSigningKey, AccessTokenMinutes = 60
    }, TimeProvider.System).CreateAccessToken(user).Token;
    internal static HttpClient Client(WuknaWebApplicationFactory factory, User user)
    {
        var client = factory.CreateClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(user));
        return client;
    }
    internal static HubConnection Connection(WuknaWebApplicationFactory factory, User user)
    {
        _ = factory.Server;
        return new HubConnectionBuilder().WithUrl(new Uri(factory.Server.BaseAddress, ChatHub.Path), options => {
            options.Transports = HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            options.AccessTokenProvider = () => Task.FromResult<string?>(Token(user));
        }).Build();
    }
    internal static async Task<ChatJoinedDto> Join(HubConnection connection, SeedData seed, CancellationToken ct)
    {
        await connection.StartAsync(ct); return await connection.InvokeAsync<ChatJoinedDto>("JoinBoard", seed.Board.Id, ct);
    }
    internal static Channel<T> Listen<T>(HubConnection connection, string name)
    {
        var channel = Channel.CreateUnbounded<T>(); connection.On<T>(name, item => channel.Writer.TryWrite(item)); return channel;
    }
    internal static async Task<T> Read<T>(Channel<T> channel, CancellationToken ct) =>
        await channel.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
    internal static async Task None<T>(Channel<T> channel, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.Reader.ReadAsync(deadline.Token).AsTask());
    }
    internal static async Task<ChatJoinedDto> State(HttpClient http, SeedData seed, CancellationToken ct) =>
        Assert.IsType<ChatJoinedDto>(await http.GetFromJsonAsync<ChatJoinedDto>(Path(seed) + "/state", ct));
    internal static Task<HttpResponseMessage> Settings(HttpClient http, SeedData seed, int seconds, long revision, CancellationToken ct) =>
        http.PutAsJsonAsync(Path(seed) + "/settings", new SetChatSettingsRequest(seconds, revision), ct);
    internal static Task<HttpResponseMessage> Mute(HttpClient http, SeedData seed, User target, ChatJoinedDto state,
        bool muted, CancellationToken ct, DateTimeOffset? until = null) => http.PutAsJsonAsync(Path(seed) + $"/members/{target.Id}/mute",
            new SetChatMuteRequest(muted, until, state.MembershipInstanceId, state.ModerationRevision), ct);
    internal static Task<HttpResponseMessage> Send(HttpClient http, SeedData seed, CancellationToken ct, Guid? operation = null) =>
        http.PostAsJsonAsync(Path(seed) + "/messages", new SendChatMessageRequest(operation ?? Guid.NewGuid(), "Message"), ct);
    internal static Task<HttpResponseMessage> Invite(HttpClient http, SeedData seed, User user, CancellationToken ct, bool edit = false) =>
        http.PutAsJsonAsync($"/api/boards/{seed.Board.Id}/guests", new SetGuestAccessRequest(user.Email!, edit), ct);
    internal static Task<HttpResponseMessage> Remove(HttpClient http, SeedData seed, User user, CancellationToken ct) =>
        http.DeleteAsync($"/api/boards/{seed.Board.Id}/guests/{user.Id}", ct);
    internal static async Task Dispatch(WuknaWebApplicationFactory factory, CancellationToken ct) =>
        await factory.Services.GetRequiredService<ChatOutboxDispatcher>().ProcessBatchAsync(ct);
    internal sealed class Factory : WuknaWebApplicationFactory
    {
        private readonly PostgresFixture postgres;
        private readonly IInterceptor? interceptor;
        public Factory(PostgresFixture postgres, ManualTimeProvider clock, IInterceptor? interceptor = null) : base(postgres, clock)
        { this.postgres = postgres; this.interceptor = interceptor; }
        public int? MaxGuests { get; set; }
        public int MembershipRate { get; set; } = 1000;
        public int ModerationRate { get; set; } = 1000;
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => {
                services.PostConfigure<BoardOptions>(options => {
                    if (MaxGuests is { } limit) options.MaxGuests = limit;
                    options.MembershipWritesPerMinute = MembershipRate;
                });
                services.PostConfigure<ChatOptions>(options => options.ModerationWritesPerMinute = ModerationRate);
                if (interceptor is null) return;
                services.RemoveAll<WuknaDbContext>(); services.RemoveAll<DbContextOptions<WuknaDbContext>>();
                services.AddDbContext<WuknaDbContext>(options => options.UseNpgsql(postgres.ConnectionString).UseSnakeCaseNamingConvention().AddInterceptors(interceptor));
            });
        }
    }
}
