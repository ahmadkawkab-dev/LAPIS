namespace Wukna.IntegrationTests;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Wukna.Features.Board;
using Wukna.Features.Realtime;
using Xunit;
using static ChatControlTestSupport;

public sealed class BoardAppearanceEndpointTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("sage")]
    [InlineData("blue")]
    [InlineData("lavender")]
    [InlineData("clay")]
    [InlineData("gold")]
    [InlineData("rose")]
    public async Task Owner_color_is_persisted_and_visible_in_every_members_board_reads(string color)
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock);
        using var owner = Client(factory, seed.Owner);
        using var viewer = Client(factory, seed.Guest);
        seed.Clock.Advance(TimeSpan.FromSeconds(1));
        using var response = await owner.PutAsJsonAsync(Url(seed), new SetBoardAppearanceRequest(color, 0), ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var changed = Assert.IsType<BoardDetailDto>(await response.Content.ReadFromJsonAsync<BoardDetailDto>(ct));
        Assert.Equal(color, changed.CardColor);
        Assert.Equal(1, changed.CardColorVersion);
        Assert.Equal(seed.Clock.GetUtcNow(), changed.UpdatedAt);
        var detail = Assert.IsType<BoardDetailDto>(await viewer.GetFromJsonAsync<BoardDetailDto>($"/api/boards/{seed.Board.Id}", ct));
        var summary = Assert.Single((await viewer.GetFromJsonAsync<BoardListItemDto[]>("/api/boards", ct))!);
        Assert.Equal(color, detail.CardColor);
        Assert.Equal(color, summary.CardColor);
        Assert.Equal(1, summary.CardColorVersion);
        await using var db = postgres.CreateContext();
        Assert.Equal(color, (await db.Boards.AsNoTracking().SingleAsync(ct)).CardColor);
        Assert.Empty(await db.Notes.ToArrayAsync(ct));
    }

    [Fact]
    public async Task Editors_can_change_color_but_viewers_outsiders_and_anonymous_users_cannot()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock);
        using var owner = Client(factory, seed.Owner);
        using var editor = Client(factory, seed.Peer);
        using var viewer = Client(factory, seed.Guest);
        using var outsider = Client(factory, seed.Outsider);
        using var anonymous = factory.CreateClient();
        using var permission = await Invite(owner, seed, seed.Peer, ct, edit: true);
        permission.EnsureSuccessStatusCode();
        using var allowed = await editor.PutAsJsonAsync(Url(seed), new SetBoardAppearanceRequest("rose", 0), ct);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using var denied = await viewer.PutAsJsonAsync(Url(seed), new SetBoardAppearanceRequest("gold", 1), ct);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var hidden = await outsider.PutAsJsonAsync(Url(seed), new SetBoardAppearanceRequest("gold", 1), ct);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using var missing = await owner.PutAsJsonAsync($"/api/boards/{Guid.NewGuid()}/appearance", new SetBoardAppearanceRequest("gold", 0), ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var unauthenticated = await anonymous.PutAsJsonAsync(Url(seed), new SetBoardAppearanceRequest("gold", 1), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        using var downgrade = await owner.PatchAsJsonAsync($"/api/boards/{seed.Board.Id}/members/{seed.Peer.Id}", new SetMemberPermissionRequest(false), ct);
        downgrade.EnsureSuccessStatusCode();
        using var downgraded = await editor.PutAsJsonAsync(Url(seed), new SetBoardAppearanceRequest("blue", 1), ct);
        Assert.Equal(HttpStatusCode.Forbidden, downgraded.StatusCode);
        using var removed = await Remove(owner, seed, seed.Peer, ct);
        removed.EnsureSuccessStatusCode();
        using var revoked = await editor.PutAsJsonAsync(Url(seed), new SetBoardAppearanceRequest("blue", 1), ct);
        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
        await using var db = postgres.CreateContext();
        Assert.Equal("rose", (await db.Boards.AsNoTracking().SingleAsync(ct)).CardColor);
    }

    [Fact]
    public async Task Invalid_values_missing_fields_and_negative_versions_do_not_mutate_board()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock);
        using var owner = Client(factory, seed.Owner);
        foreach (var payload in new object[] {
            new { cardColor = "#FFFFFF", expectedVersion = 0 },
            new { cardColor = "unknown", expectedVersion = 0 },
            new { cardColor = "sage", expectedVersion = -1 },
            new { cardColor = "sage" }, new { expectedVersion = 0 } })
        {
            using var response = await owner.PutAsJsonAsync(Url(seed), payload, ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        await using var db = postgres.CreateContext();
        var board = await db.Boards.AsNoTracking().SingleAsync(ct);
        Assert.Null(board.CardColor);
        Assert.Equal(0, board.CardColorVersion);
        Assert.Equal(seed.Board.UpdatedAt, board.UpdatedAt);
    }

    [Fact]
    public async Task Concurrent_writers_cannot_overwrite_a_newer_color_and_reset_is_versioned()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock);
        using var owner = Client(factory, seed.Owner);
        using var peer = Client(factory, seed.Peer);
        using var permission = await Invite(owner, seed, seed.Peer, ct, edit: true);
        permission.EnsureSuccessStatusCode();
        var responses = await Task.WhenAll(
            owner.PutAsJsonAsync(Url(seed), new SetBoardAppearanceRequest("blue", 0), ct),
            peer.PutAsJsonAsync(Url(seed), new SetBoardAppearanceRequest("clay", 0), ct));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            var conflict = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
            Assert.Contains("board_appearance_conflict", await conflict.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }
        finally { foreach (var response in responses) response.Dispose(); }
        var current = Assert.IsType<BoardDetailDto>(await owner.GetFromJsonAsync<BoardDetailDto>($"/api/boards/{seed.Board.Id}", ct));
        Assert.Equal(1, current.CardColorVersion);
        var beforeNoOp = current.UpdatedAt;
        seed.Clock.Advance(TimeSpan.FromSeconds(1));
        using var same = await owner.PutAsJsonAsync(Url(seed), new SetBoardAppearanceRequest(current.CardColor, 1), ct);
        var noOp = Assert.IsType<BoardDetailDto>(await same.Content.ReadFromJsonAsync<BoardDetailDto>(ct));
        Assert.Equal(1, noOp.CardColorVersion);
        Assert.Equal(beforeNoOp, noOp.UpdatedAt);
        using var reset = await peer.PutAsJsonAsync(Url(seed), new SetBoardAppearanceRequest(null, 1), ct);
        var automatic = Assert.IsType<BoardDetailDto>(await reset.Content.ReadFromJsonAsync<BoardDetailDto>(ct));
        Assert.Null(automatic.CardColor);
        Assert.Equal(2, automatic.CardColorVersion);
        using var rename = await owner.PatchAsJsonAsync($"/api/boards/{seed.Board.Id}", new RenameBoardRequest("Renamed"), ct);
        var renamed = Assert.IsType<BoardDetailDto>(await rename.Content.ReadFromJsonAsync<BoardDetailDto>(ct));
        Assert.Equal(2, renamed.CardColorVersion);
    }

    [Fact]
    public async Task Realtime_update_and_summaries_reach_members_after_commit_and_exclude_outsiders()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await Seed(postgres, ct);
        await using var factory = new Factory(postgres, seed.Clock);
        await using var viewer = BoardConnection(factory, seed.Guest);
        await using var outsider = BoardConnection(factory, seed.Outsider);
        var updates = Listen<BoardUpdatedEvent>(viewer, BoardRealtimeEvents.BoardUpdated);
        var summaries = Listen<BoardListItemDto>(viewer, BoardRealtimeEvents.BoardSummaryChanged);
        var outsiderUpdates = Listen<BoardUpdatedEvent>(outsider, BoardRealtimeEvents.BoardUpdated);
        var outsiderSummaries = Listen<BoardListItemDto>(outsider, BoardRealtimeEvents.BoardSummaryChanged);
        await viewer.StartAsync(ct);
        await outsider.StartAsync(ct);
        await viewer.InvokeAsync<BoardPresenceSnapshot>("JoinBoard", seed.Board.Id, ct);
        using var owner = Client(factory, seed.Owner);
        using var reply = await owner.PutAsJsonAsync(Url(seed), new SetBoardAppearanceRequest("lavender", 0), ct);
        reply.EnsureSuccessStatusCode();
        Assert.Equal("lavender", (await Read(updates, ct)).CardColor);
        var summary = await Read(summaries, ct);
        Assert.Equal("lavender", summary.CardColor);
        Assert.Equal(1, summary.CardColorVersion);
        await using var db = postgres.CreateContext();
        Assert.Equal("lavender", (await db.Boards.AsNoTracking().SingleAsync(ct)).CardColor);
        await None(outsiderUpdates, ct);
        await None(outsiderSummaries, ct);
    }

    [Fact]
    public async Task Migration_preserves_existing_board_and_adds_safe_automatic_defaults()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = postgres.CreateContext();
        await db.Database.EnsureDeletedAsync(ct);
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20261004180751_AddUserOnboarding", ct);
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO boards (id, title) VALUES ({id}, {"Existing board"})", ct);
        await migrator.MigrateAsync(cancellationToken: ct);
        var board = await db.Boards.SingleAsync(candidate => candidate.Id == id, ct);
        Assert.Equal("Existing board", board.Title);
        Assert.Null(board.CardColor);
        Assert.Equal(0, board.CardColorVersion);
        board.CardColor = "invalid";
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
    }

    private static string Url(SeedData seed) => $"/api/boards/{seed.Board.Id}/appearance";
    private static HubConnection BoardConnection(WuknaWebApplicationFactory factory, Wukna.Features.Users.User user) =>
        new HubConnectionBuilder().WithUrl(new Uri(factory.Server.BaseAddress, BoardHub.Path), options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            options.AccessTokenProvider = () => Task.FromResult<string?>(Token(user));
        }).Build();
}
