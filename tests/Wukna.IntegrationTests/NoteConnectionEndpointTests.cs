namespace Wukna.IntegrationTests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Wukna.Features.Auth;
using Wukna.Features.Board;
using Wukna.Features.NoteConnection;
using Wukna.Features.Notes;
using Wukna.Features.Realtime;
using Wukna.Features.Users;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Xunit;

public sealed class NoteConnectionEndpointTests(PostgresFixture postgres)
{
    private static readonly string[] Sides = ["top", "right", "bottom", "left"];
    private readonly CancellationToken cancellationToken = TestContext.Current.CancellationToken;

    [Fact]
    public async Task All_sixteen_side_pairs_persist_reload_and_remain_attached_when_cards_move()
    {
        var seed = await SeedAsync();
        await using var factory = new WuknaWebApplicationFactory(postgres, seed.Clock);
        using var client = Client(factory, seed.Owner, seed.Clock);
        var url = $"/api/boards/{seed.Board.Id}/connections";
        var expected = new List<NoteConnectionDto>();
        var index = 0;
        foreach (var sourceSide in Sides)
        foreach (var targetSide in Sides)
        {
            var pair = seed.Notes.Skip(index++ * 2).Take(2).ToArray();
            using var response = await client.PostAsJsonAsync(url,
                new CreateNoteConnectionRequest(pair[0].Id, pair[1].Id, ConnectionType.Prerequisite, sourceSide, targetSide), cancellationToken);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var dto = Assert.IsType<NoteConnectionDto>(await response.Content.ReadFromJsonAsync<NoteConnectionDto>(cancellationToken: cancellationToken));
            Assert.Equal(sourceSide, dto.SourceHandle);
            Assert.Equal(targetSide, dto.TargetHandle);
            Assert.True(dto.Version > 0);
            Assert.Equal($"\"{dto.Version}\"", response.Headers.ETag?.Tag);
            // PostgreSQL stores timestamps at microsecond precision.
            expected.Add(dto with { CreatedAt = new DateTimeOffset(dto.CreatedAt.Ticks / 10 * 10, dto.CreatedAt.Offset) });
        }
        var reloaded = await client.GetFromJsonAsync<NoteConnectionDto[]>(url, cancellationToken);
        Assert.NotNull(reloaded);
        Assert.Equal(expected.OrderBy(c => c.Id), reloaded.OrderBy(c => c.Id));
        await using (var db = postgres.CreateContext())
        {
            var note = await db.Notes.SingleAsync(n => n.Id == seed.Notes[0].Id, cancellationToken);
            note.PositionX = -2400; note.PositionY = 1300; note.Width = 420; note.Height = 360;
            await db.SaveChangesAsync(cancellationToken);
        }
        var afterMove = await client.GetFromJsonAsync<NoteConnectionDto[]>(url, cancellationToken);
        Assert.Equal(expected.OrderBy(c => c.Id), afterMove!.OrderBy(c => c.Id));
    }

    [Fact]
    public async Task Reconnection_keeps_identity_and_publishes_committed_sides_with_stale_edit_protection()
    {
        var seed = await SeedAsync();
        await using var factory = new WuknaWebApplicationFactory(postgres, seed.Clock);
        await using var hub = new HubConnectionBuilder().WithUrl(new Uri(factory.Server.BaseAddress, BoardHub.Path), options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            options.AccessTokenProvider = () => Task.FromResult<string?>(Token(seed.Owner, seed.Clock));
        }).Build();
        var createdEvent = new TaskCompletionSource<NoteConnectionDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var updatedEvent = new TaskCompletionSource<NoteConnectionDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deletedEvent = new TaskCompletionSource<ConnectionDeletedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<NoteConnectionDto>(BoardRealtimeEvents.ConnectionCreated, createdEvent.SetResult);
        hub.On<NoteConnectionDto>(BoardRealtimeEvents.ConnectionUpdated, dto => updatedEvent.TrySetResult(dto));
        hub.On<ConnectionDeletedEvent>(BoardRealtimeEvents.ConnectionDeleted, deletedEvent.SetResult);
        await hub.StartAsync(cancellationToken);
        await hub.InvokeAsync("JoinBoard", seed.Board.Id, cancellationToken);
        using var client = Client(factory, seed.Owner, seed.Clock);
        var url = $"/api/boards/{seed.Board.Id}/connections";
        using var create = await client.PostAsJsonAsync(url,
            new CreateNoteConnectionRequest(seed.Notes[0].Id, seed.Notes[1].Id, ConnectionType.Related, "top", "bottom"), cancellationToken);
        create.EnsureSuccessStatusCode();
        var original = Assert.IsType<NoteConnectionDto>(await create.Content.ReadFromJsonAsync<NoteConnectionDto>(cancellationToken: cancellationToken));
        Assert.Equal(original, await createdEvent.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
        var request = new ReconnectNoteConnectionRequest(seed.Notes[2].Id, seed.Notes[3].Id, "left", "right");
        using var change = await Patch(client, $"{url}/{original.Id}", request, original.Version);
        change.EnsureSuccessStatusCode();
        var updated = Assert.IsType<NoteConnectionDto>(await change.Content.ReadFromJsonAsync<NoteConnectionDto>(cancellationToken: cancellationToken));
        Assert.Equal(original.Id, updated.Id);
        Assert.Equal(original.Type, updated.Type);
        Assert.True(updated.Version > original.Version);
        Assert.Equal(updated, await updatedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
        await using (var db = postgres.CreateContext())
            Assert.Equal(updated, NoteConnectionDto.From(await db.NoteConnections.AsNoTracking().SingleAsync(c => c.Id == original.Id, cancellationToken)));
        // Moving to any other side of the same two cards is a valid reconnect.
        foreach (var side in Sides)
        {
            using var next = await Patch(client, $"{url}/{original.Id}", request with { SourceHandle = side, TargetHandle = side }, updated.Version);
            next.EnsureSuccessStatusCode();
            updated = Assert.IsType<NoteConnectionDto>(await next.Content.ReadFromJsonAsync<NoteConnectionDto>(cancellationToken: cancellationToken));
            Assert.Equal(side, updated.SourceHandle); Assert.Equal(side, updated.TargetHandle);
        }
        using var stale = await Patch(client, $"{url}/{original.Id}", request, original.Version);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var missing = await Patch(client, $"{url}/{original.Id}", request, null);
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        foreach (var invalid in new[] {
            request with { SourceHandle = "middle" }, request with { TargetHandle = null! },
            request with { TargetNoteId = request.SourceNoteId },
            request with { TargetNoteId = seed.Foreign.Id }, request with { TargetNoteId = seed.Checklist.Id } })
        {
            using var rejected = await Patch(client, $"{url}/{original.Id}", invalid, updated.Version);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        using var viewer = Client(factory, seed.Viewer, seed.Clock);
        using var forbidden = await Patch(viewer, $"{url}/{original.Id}", request, updated.Version);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var outsider = Client(factory, seed.Outsider, seed.Clock);
        using var hidden = await Patch(outsider, $"{url}/{original.Id}", request, updated.Version);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        var reload = await client.GetFromJsonAsync<NoteConnectionDto[]>(url, cancellationToken);
        Assert.Equal(updated, Assert.Single(reload!));
        using var delete = await client.DeleteAsync($"{url}/{original.Id}", cancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        var deleted = await deletedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Equal(updated.Id, deleted.ConnectionId); Assert.Equal(updated.Version, deleted.Version);
        Assert.Empty((await client.GetFromJsonAsync<NoteConnectionDto[]>(url, cancellationToken))!);
    }

    [Fact]
    public async Task Concurrent_reversed_duplicates_and_reconnection_to_an_existing_pair_are_rejected()
    {
        var seed = await SeedAsync();
        await using var factory = new WuknaWebApplicationFactory(postgres, seed.Clock);
        using var client = Client(factory, seed.Owner, seed.Clock);
        var url = $"/api/boards/{seed.Board.Id}/connections";
        var responses = await Task.WhenAll(
            client.PostAsJsonAsync(url, new CreateNoteConnectionRequest(seed.Notes[0].Id, seed.Notes[1].Id, ConnectionType.Related, "left", "top"), cancellationToken),
            client.PostAsJsonAsync(url, new CreateNoteConnectionRequest(seed.Notes[1].Id, seed.Notes[0].Id, ConnectionType.Prerequisite, "bottom", "right"), cancellationToken));
        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        }
        finally { foreach (var response in responses) response.Dispose(); }
        using var second = await client.PostAsJsonAsync(url, new CreateNoteConnectionRequest(seed.Notes[2].Id, seed.Notes[3].Id, ConnectionType.Related), cancellationToken);
        second.EnsureSuccessStatusCode();
        var dto = Assert.IsType<NoteConnectionDto>(await second.Content.ReadFromJsonAsync<NoteConnectionDto>(cancellationToken: cancellationToken));
        using var conflict = await Patch(client, $"{url}/{dto.Id}", new ReconnectNoteConnectionRequest(seed.Notes[1].Id, seed.Notes[0].Id, "top", "left"), dto.Version);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var self = await client.PostAsJsonAsync(url, new CreateNoteConnectionRequest(seed.Notes[0].Id, seed.Notes[0].Id, ConnectionType.Related), cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
        Assert.Equal(2, (await client.GetFromJsonAsync<NoteConnectionDto[]>(url, cancellationToken))!.Length);
    }

    private async Task<HttpResponseMessage> Patch(HttpClient client, string url, ReconnectNoteConnectionRequest body, uint? version)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonContent.Create(body) };
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return await client.SendAsync(request, cancellationToken);
    }
    private async Task<Seed> SeedAsync()
    {
        await postgres.ResetAsync(cancellationToken);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var owner = NewUser("connector-owner@wukna.test"); var viewer = NewUser("connector-viewer@wukna.test"); var outsider = NewUser("connector-outsider@wukna.test");
        var board = new Board { Title = "Four sides", CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
        board.Memberships.Add(new BoardMembership { User = owner, Role = BoardRole.Owner, CanEdit = true });
        board.Memberships.Add(new BoardMembership { User = viewer, Role = BoardRole.Guest, CanEdit = false });
        var notes = Enumerable.Range(0, 32).Select(i => new Note { Board = board, Kind = i % 2 == 0 ? NoteKind.Standalone : NoteKind.List, Title = $"Card {i}", PositionX = i * 320 - 500, PositionY = -300, Width = 280, Height = 220 }).ToArray();
        var foreign = new Note { Board = new Board { Title = "Other board" }, Kind = NoteKind.Standalone, Title = "Foreign", PositionX = 0, PositionY = 0 };
        var checklist = new Note { Board = board, Kind = NoteKind.ChecklistItem, ParentNote = notes[1], Title = "Child", PositionX = null, PositionY = null };
        await using var db = postgres.CreateContext();
        db.Users.AddRange(owner, viewer, outsider); db.Notes.AddRange(notes); db.Notes.AddRange(foreign, checklist);
        await db.SaveChangesAsync(cancellationToken);
        return new Seed(clock, owner, viewer, outsider, board, notes, foreign, checklist);
    }
    private static User NewUser(string email) => new() { Id = Guid.NewGuid(), Email = email, NormalizedEmail = email.ToUpperInvariant(), UserName = email, NormalizedUserName = email.ToUpperInvariant(), SecurityStamp = Guid.NewGuid().ToString("N") };
    private static string Token(User user, TimeProvider clock) => new JwtTokenGenerator(new JwtOptions { Issuer = WuknaWebApplicationFactory.JwtIssuer, Audience = WuknaWebApplicationFactory.JwtAudience, SigningKey = WuknaWebApplicationFactory.JwtSigningKey, AccessTokenMinutes = 60 }, clock).CreateAccessToken(user).Token;
    private static HttpClient Client(WuknaWebApplicationFactory factory, User user, TimeProvider clock) { var client = factory.CreateClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(user, clock)); return client; }
    private sealed record Seed(ManualTimeProvider Clock, User Owner, User Viewer, User Outsider, Board Board, Note[] Notes, Note Foreign, Note Checklist);
}
