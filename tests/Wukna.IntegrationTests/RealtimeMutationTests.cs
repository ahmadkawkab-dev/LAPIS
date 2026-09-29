namespace Wukna.IntegrationTests;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Wukna.Features.Auth;
using Wukna.Features.Board;
using Wukna.Features.NoteConnection;
using Wukna.Features.Notes;
using Wukna.Features.Realtime;
using Wukna.Features.Users;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

public sealed class RealtimeMutationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Note_and_task_geometry_is_bounded_in_storage_and_realtime_events()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(includeGuest: false, cancellationToken);
        var publisher = new RecordingPublisher(postgres);
        await using var factory = FactoryWithPublisher(seed.Clock, publisher);
        using var client = AuthorizedClient(factory, seed.Owner, seed.Clock);
        var url = $"/api/boards/{seed.Board.Id}/notes";

        foreach (var kind in new[] { NoteKind.Standalone, NoteKind.List })
        foreach (var x in new[] { -100000d, 100000d })
        foreach (var y in new[] { -100000d, 100000d })
        {
            using var response = await client.PostAsJsonAsync(url,
                new { kind, title = "Bounded card", positionX = x, positionY = y, width = 280, height = 220 },
                cancellationToken);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var created = Assert.IsType<NoteDto>(await response.Content.ReadFromJsonAsync<NoteDto>(cancellationToken: cancellationToken));
            Assert.Equal(x < 0 ? BoardWorkspaceBounds.Left : BoardWorkspaceBounds.Right - created.Width, created.PositionX);
            Assert.Equal(y < 0 ? BoardWorkspaceBounds.Top : BoardWorkspaceBounds.Bottom - created.Height, created.PositionY);
            Assert.Contains(publisher.Events, item => item.EventName == BoardRealtimeEvents.NoteCreated && Equals(item.Message, created));

            using var patch = new HttpRequestMessage(HttpMethod.Patch, $"{url}/{created.Id}")
            {
                Content = JsonContent.Create(new { positionX = -x, positionY = -y, width = 420, height = 360 })
            };
            patch.Headers.TryAddWithoutValidation("If-Match", $"\"{created.Version}\"");
            using var patched = await client.SendAsync(patch, cancellationToken);
            patched.EnsureSuccessStatusCode();
            var moved = Assert.IsType<NoteDto>(await patched.Content.ReadFromJsonAsync<NoteDto>(cancellationToken: cancellationToken));
            Assert.Equal(x > 0 ? BoardWorkspaceBounds.Left : BoardWorkspaceBounds.Right - moved.Width, moved.PositionX);
            Assert.Equal(y > 0 ? BoardWorkspaceBounds.Top : BoardWorkspaceBounds.Bottom - moved.Height, moved.PositionY);
            Assert.True(moved.Version > created.Version);
            Assert.Contains(publisher.Events, item => item.EventName == BoardRealtimeEvents.NoteUpdated && Equals(item.Message, moved));
            await using var db = postgres.CreateContext();
            var saved = await db.Notes.AsNoTracking().SingleAsync(note => note.Id == moved.Id, cancellationToken);
            Assert.Equal(moved.PositionX, saved.PositionX);
            Assert.Equal(moved.PositionY, saved.PositionY);
            Assert.Equal(moved.Width, saved.Width);
            Assert.Equal(moved.Height, saved.Height);

            using var oversizedPatch = new HttpRequestMessage(HttpMethod.Patch, $"{url}/{moved.Id}")
            {
                Content = JsonContent.Create(new { width = 20000, height = 12000 })
            };
            oversizedPatch.Headers.TryAddWithoutValidation("If-Match", $"\"{moved.Version}\"");
            using var resized = await client.SendAsync(oversizedPatch, cancellationToken);
            resized.EnsureSuccessStatusCode();
            var oversized = Assert.IsType<NoteDto>(await resized.Content.ReadFromJsonAsync<NoteDto>(cancellationToken: cancellationToken));
            Assert.Equal(BoardWorkspaceBounds.Right - BoardWorkspaceBounds.Left, oversized.Width);
            Assert.Equal(BoardWorkspaceBounds.Bottom - BoardWorkspaceBounds.Top, oversized.Height);
            Assert.Equal(BoardWorkspaceBounds.Left, oversized.PositionX);
            Assert.Equal(BoardWorkspaceBounds.Top, oversized.PositionY);
        }
        Assert.True(publisher.SawCommittedNoteState);
    }

    [Fact]
    public async Task Xmin_is_an_atomic_database_concurrency_token_in_generated_updates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(includeGuest: false, cancellationToken);
        var commands = new CommandCounterInterceptor();

        await using var first = postgres.CreateContext();
        await using var second = postgres.CreateContext(commands);
        var firstCopy = await first.Notes.SingleAsync(
            note => note.Id == seed.Note.Id, cancellationToken);
        var secondCopy = await second.Notes.SingleAsync(
            note => note.Id == seed.Note.Id, cancellationToken);

        firstCopy.Title = "First writer";
        await first.SaveChangesAsync(cancellationToken);
        secondCopy.Title = "Stale second writer";

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => second.SaveChangesAsync(cancellationToken));

        var update = Assert.Single(commands.Commands, command =>
            command.Contains("UPDATE notes", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("xmin", update, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHERE", update, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RETURNING xmin", update, StringComparison.OrdinalIgnoreCase);

        await using var verification = postgres.CreateContext();
        Assert.Equal("First writer", await verification.Notes.AsNoTracking()
            .Where(note => note.Id == seed.Note.Id)
            .Select(note => note.Title)
            .SingleAsync(cancellationToken));
    }

    [Fact]
    public async Task Mutations_publish_after_commit_and_failures_or_conflicts_do_not_publish()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(includeGuest: false, cancellationToken);
        var publisher = new RecordingPublisher(postgres);
        await using var factory = FactoryWithPublisher(seed.Clock, publisher);
        using var client = AuthorizedClient(factory, seed.Owner, seed.Clock);

        using var invalid = await client.PatchAsJsonAsync(
            $"/api/boards/{seed.Board.Id}", new { title = "" }, cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Empty(publisher.Events);

        using var patch = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/boards/{seed.Board.Id}/notes/{seed.Note.Id}")
        {
            Content = JsonContent.Create(new { title = "Committed title" })
        };
        patch.Headers.TryAddWithoutValidation("If-Match", $"\"{seed.Note.Version}\"");
        using var response = await client.SendAsync(patch, cancellationToken);
        response.EnsureSuccessStatusCode();
        var updated = await response.Content.ReadFromJsonAsync<NoteDto>(
            cancellationToken: cancellationToken);
        Assert.NotNull(updated);
        Assert.True(publisher.SawCommittedNoteState);
        Assert.Contains(publisher.Events, item =>
            item.EventName == BoardRealtimeEvents.NoteUpdated &&
            item.Message is NoteDto note && note.Version == updated.Version);

        var beforeConflict = publisher.Events.Count;
        using var stale = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/boards/{seed.Board.Id}/notes/{seed.Note.Id}")
        {
            Content = JsonContent.Create(new { title = "Must not publish" })
        };
        stale.Headers.TryAddWithoutValidation("If-Match", $"\"{seed.Note.Version}\"");
        using var conflict = await client.SendAsync(stale, cancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(beforeConflict, publisher.Events.Count);

        using var createdResponse = await client.PostAsJsonAsync(
            $"/api/boards/{seed.Board.Id}/notes",
            new { kind = 0, title = "Connected note", positionX = 100, positionY = 100 },
            cancellationToken);
        createdResponse.EnsureSuccessStatusCode();
        var created = Assert.IsType<NoteDto>(await createdResponse.Content
            .ReadFromJsonAsync<NoteDto>(cancellationToken: cancellationToken));

        using var connectionResponse = await client.PostAsJsonAsync(
            $"/api/boards/{seed.Board.Id}/connections",
            new { sourceNoteId = seed.Note.Id, targetNoteId = created.Id, type = 0 },
            cancellationToken);
        connectionResponse.EnsureSuccessStatusCode();
        var connection = Assert.IsType<NoteConnectionDto>(await connectionResponse.Content
            .ReadFromJsonAsync<NoteConnectionDto>(cancellationToken: cancellationToken));
        using var deleteConnection = await client.DeleteAsync(
            $"/api/boards/{seed.Board.Id}/connections/{connection.Id}", cancellationToken);
        deleteConnection.EnsureSuccessStatusCode();

        using var deleteNoteRequest = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/boards/{seed.Board.Id}/notes/{created.Id}");
        deleteNoteRequest.Headers.TryAddWithoutValidation("If-Match", $"\"{created.Version}\"");
        using var deleteNote = await client.SendAsync(deleteNoteRequest, cancellationToken);
        deleteNote.EnsureSuccessStatusCode();

        using var rename = await client.PatchAsJsonAsync(
            $"/api/boards/{seed.Board.Id}", new { title = "Renamed" }, cancellationToken);
        rename.EnsureSuccessStatusCode();
        using var addGuest = await client.PutAsJsonAsync(
            $"/api/boards/{seed.Board.Id}/guests",
            new { email = seed.Guest.Email, canEdit = false },
            cancellationToken);
        addGuest.EnsureSuccessStatusCode();

        var eventNames = publisher.Events.Select(item => item.EventName).ToHashSet();
        Assert.Contains(BoardRealtimeEvents.NoteCreated, eventNames);
        Assert.Contains(BoardRealtimeEvents.NoteUpdated, eventNames);
        Assert.Contains(BoardRealtimeEvents.NoteDeleted, eventNames);
        Assert.Contains(BoardRealtimeEvents.ConnectionCreated, eventNames);
        Assert.Contains(BoardRealtimeEvents.ConnectionDeleted, eventNames);
        Assert.Contains(BoardRealtimeEvents.BoardUpdated, eventNames);
        Assert.Contains(BoardRealtimeEvents.MembersChanged, eventNames);
        Assert.Contains(BoardRealtimeEvents.BoardSummaryChanged, eventNames);
    }

    [Fact]
    public async Task Publisher_failure_does_not_turn_a_committed_mutation_into_a_false_failure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(includeGuest: false, cancellationToken);
        var publisher = new RecordingPublisher(postgres) { ThrowOnPublish = true };
        var logger = new RecordingLogger();
        await using var factory = FactoryWithPublisher(seed.Clock, publisher, logger);
        using var client = AuthorizedClient(factory, seed.Owner, seed.Clock);

        using var response = await client.PatchAsJsonAsync(
            $"/api/boards/{seed.Board.Id}",
            new { title = "Committed despite transport failure" },
            cancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.True(publisher.Attempts >= 2);
        Assert.Contains(logger.Messages, message =>
            message.Contains("Realtime publication failed after commit", StringComparison.Ordinal));
        await using var verification = postgres.CreateContext();
        Assert.Equal("Committed despite transport failure", await verification.Boards.AsNoTracking()
            .Where(board => board.Id == seed.Board.Id)
            .Select(board => board.Title)
            .SingleAsync(cancellationToken));
    }

    [Fact]
    public async Task Removing_a_connected_member_revokes_group_access_and_summary_visibility()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(includeGuest: true, cancellationToken);
        await using var factory = new WuknaWebApplicationFactory(postgres, seed.Clock);
        await using var guestConnection = Connection(
            factory, AccessToken(seed.Guest!, seed.Clock));
        var revoked = new TaskCompletionSource<BoardAccessRevokedEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var summaryRemoved = new TaskCompletionSource<BoardSummaryRemovedEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var forbiddenProbe = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        guestConnection.On<BoardAccessRevokedEvent>(
            BoardRealtimeEvents.BoardAccessRevoked, revoked.SetResult);
        guestConnection.On<BoardSummaryRemovedEvent>(
            BoardRealtimeEvents.BoardSummaryRemoved, summaryRemoved.SetResult);
        guestConnection.On<string>("ProtectedProbe", forbiddenProbe.SetResult);
        await guestConnection.StartAsync(cancellationToken);
        await guestConnection.InvokeAsync("JoinBoard", seed.Board.Id, cancellationToken);

        using var ownerClient = AuthorizedClient(factory, seed.Owner, seed.Clock);
        using var response = await ownerClient.DeleteAsync(
            $"/api/boards/{seed.Board.Id}/guests/{seed.Guest!.Id}",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        Assert.Equal(seed.Board.Id, (await revoked.Task.WaitAsync(
            TimeSpan.FromSeconds(5), cancellationToken)).BoardId);
        Assert.Equal(seed.Board.Id, (await summaryRemoved.Task.WaitAsync(
            TimeSpan.FromSeconds(5), cancellationToken)).BoardId);

        var publisher = factory.Services.GetRequiredService<IBoardRealtimePublisher>();
        await publisher.PublishBoardAsync(
            seed.Board.Id, "ProtectedProbe", "must-not-arrive", cancellationToken);
        await Assert.ThrowsAsync<TimeoutException>(() => forbiddenProbe.Task.WaitAsync(
            TimeSpan.FromMilliseconds(500), cancellationToken));
    }

    [Fact]
    public async Task Downgrading_a_connected_editor_stops_active_editing_but_keeps_view_access()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(includeGuest: true, cancellationToken);
        await using var factory = new WuknaWebApplicationFactory(postgres, seed.Clock);
        await using var guestConnection = Connection(factory, AccessToken(seed.Guest!, seed.Clock));
        var stopped = new TaskCompletionSource<NoteEditingStoppedEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var previewEnded = new TaskCompletionSource<NoteGeometryPreviewEndedEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = new TaskCompletionSource<MembersChangedEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var viewerProbe = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        guestConnection.On<NoteEditingStoppedEvent>(
            BoardRealtimeEvents.NoteEditingStopped, stopped.SetResult);
        guestConnection.On<NoteGeometryPreviewEndedEvent>(
            BoardRealtimeEvents.NoteGeometryPreviewEnded, previewEnded.SetResult);
        guestConnection.On<MembersChangedEvent>(
            BoardRealtimeEvents.MembersChanged, changed.SetResult);
        guestConnection.On<string>("ViewerProbe", viewerProbe.SetResult);
        await guestConnection.StartAsync(cancellationToken);
        await guestConnection.InvokeAsync("JoinBoard", seed.Board.Id, cancellationToken);
        await guestConnection.InvokeAsync("StartNoteEditing",
            new StartNoteEditingRequest(seed.Board.Id, seed.Note.Id, 1), cancellationToken);
        await guestConnection.InvokeAsync("PreviewNoteGeometry",
            new NoteGeometryPreviewRequest(seed.Board.Id, seed.Note.Id,
                NoteGeometryOperation.Drag, 25, 35, null, null, seed.Note.Version, 1),
            cancellationToken);

        using var ownerClient = AuthorizedClient(factory, seed.Owner, seed.Clock);
        using var response = await ownerClient.PatchAsJsonAsync(
            $"/api/boards/{seed.Board.Id}/members/{seed.Guest!.Id}",
            new { canEdit = false }, cancellationToken);
        response.EnsureSuccessStatusCode();

        Assert.Equal(seed.Guest.Id, (await stopped.Task.WaitAsync(
            TimeSpan.FromSeconds(5), cancellationToken)).UserId);
        Assert.Equal(seed.Guest.Id, (await previewEnded.Task.WaitAsync(
            TimeSpan.FromSeconds(5), cancellationToken)).UserId);
        Assert.Equal(seed.Board.Id, (await changed.Task.WaitAsync(
            TimeSpan.FromSeconds(5), cancellationToken)).BoardId);
        var denied = await Assert.ThrowsAsync<HubException>(() =>
            guestConnection.InvokeAsync("StartNoteEditing",
                new StartNoteEditingRequest(seed.Board.Id, seed.Note.Id, 2), cancellationToken));
        Assert.Contains("Forbidden", denied.Message, StringComparison.Ordinal);
        var deniedPreview = await Assert.ThrowsAsync<HubException>(() =>
            guestConnection.InvokeAsync("PreviewNoteGeometry",
                new NoteGeometryPreviewRequest(seed.Board.Id, seed.Note.Id,
                    NoteGeometryOperation.Drag, 30, 40, null, null, seed.Note.Version, 2),
                cancellationToken));
        Assert.Contains("Forbidden", deniedPreview.Message, StringComparison.Ordinal);

        var publisher = factory.Services.GetRequiredService<IBoardRealtimePublisher>();
        await publisher.PublishBoardAsync(seed.Board.Id, "ViewerProbe", "still-viewing", cancellationToken);
        Assert.Equal("still-viewing", await viewerProbe.Task.WaitAsync(
            TimeSpan.FromSeconds(5), cancellationToken));
    }

    private WuknaWebApplicationFactory FactoryWithPublisher(
        ManualTimeProvider clock,
        IBoardRealtimePublisher publisher,
        ILogger<BoardRealtimeDispatcher>? logger = null)
    {
        return new ConfiguredFactory(postgres, clock, publisher, logger);
    }

    private async Task<Seed> SeedAsync(bool includeGuest, CancellationToken cancellationToken)
    {
        await postgres.ResetAsync(cancellationToken);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var owner = User("mutation-owner@wukna.test");
        var guest = User("mutation-guest@wukna.test");
        var board = new Board
        {
            Title = "Realtime mutations",
            CreatedAt = clock.GetUtcNow(),
            UpdatedAt = clock.GetUtcNow()
        };
        board.Memberships.Add(new BoardMembership
        {
            User = owner,
            Role = BoardRole.Owner,
            CanEdit = true
        });
        if (includeGuest)
        {
            board.Memberships.Add(new BoardMembership
            {
                User = guest,
                Role = BoardRole.Guest,
                CanEdit = true
            });
        }
        var note = new Note
        {
            Board = board,
            Kind = NoteKind.Standalone,
            Title = "Original",
            PositionX = 0,
            PositionY = 0
        };

        await using (var db = postgres.CreateContext())
        {
            db.Users.AddRange(owner, guest);
            db.Notes.Add(note);
            await db.SaveChangesAsync(cancellationToken);
        }
        return new Seed(clock, owner, guest, board, note);
    }

    private static HttpClient AuthorizedClient(
        WuknaWebApplicationFactory factory,
        User user,
        TimeProvider clock)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", AccessToken(user, clock));
        return client;
    }

    private static HubConnection Connection(
        WuknaWebApplicationFactory factory,
        string accessToken)
    {
        _ = factory.Server;
        return new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, BoardHub.Path), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .Build();
    }

    private static string AccessToken(User user, TimeProvider clock)
    {
        var options = new JwtOptions
        {
            Issuer = WuknaWebApplicationFactory.JwtIssuer,
            Audience = WuknaWebApplicationFactory.JwtAudience,
            SigningKey = WuknaWebApplicationFactory.JwtSigningKey,
            AccessTokenMinutes = 60
        };
        return new JwtTokenGenerator(options, clock).CreateAccessToken(user).Token;
    }

    private static User User(string email) => new()
    {
        Id = Guid.NewGuid(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        UserName = email,
        NormalizedUserName = email.ToUpperInvariant(),
        SecurityStamp = Guid.NewGuid().ToString("N")
    };

    private sealed record Seed(
        ManualTimeProvider Clock,
        User Owner,
        User Guest,
        Board Board,
        Note Note);

    private sealed record PublishedEvent(string EventName, object? Message);

    private sealed class RecordingPublisher(PostgresFixture postgres) : IBoardRealtimePublisher
    {
        private int attempts;
        public ConcurrentQueue<PublishedEvent> Events { get; } = new();
        public int Attempts => attempts;
        public bool ThrowOnPublish { get; init; }
        public bool SawCommittedNoteState { get; private set; }

        public async Task PublishBoardAsync<TEvent>(
            Guid boardId,
            string eventName,
            TEvent message,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref attempts);
            Events.Enqueue(new PublishedEvent(eventName, message));
            if (eventName == BoardRealtimeEvents.NoteUpdated && message is NoteDto note)
            {
                await using var db = postgres.CreateContext();
                SawCommittedNoteState = await db.Notes.AsNoTracking().AnyAsync(
                    candidate => candidate.Id == note.Id &&
                                 candidate.Title == note.Title &&
                                 candidate.Version == note.Version,
                    cancellationToken);
            }
            if (ThrowOnPublish) throw new InvalidOperationException("SignalR unavailable");
        }

        public Task PublishUsersAsync<TEvent>(
            IReadOnlyCollection<Guid> userIds,
            string eventName,
            TEvent message,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref attempts);
            Events.Enqueue(new PublishedEvent(eventName, message));
            return ThrowOnPublish
                ? Task.FromException(new InvalidOperationException("SignalR unavailable"))
                : Task.CompletedTask;
        }

        public Task RevokeBoardAccessAsync(
            Guid boardId,
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref attempts);
            return ThrowOnPublish
                ? Task.FromException(new InvalidOperationException("SignalR unavailable"))
                : Task.CompletedTask;
        }

        public Task StopBoardEditingAsync(
            Guid boardId, Guid userId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref attempts);
            return ThrowOnPublish
                ? Task.FromException(new InvalidOperationException("SignalR unavailable"))
                : Task.CompletedTask;
        }
    }

    private sealed class ConfiguredFactory(
        PostgresFixture postgres,
        ManualTimeProvider clock,
        IBoardRealtimePublisher publisher,
        ILogger<BoardRealtimeDispatcher>? logger) : WuknaWebApplicationFactory(postgres, clock)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IBoardRealtimePublisher>();
                services.AddSingleton(publisher);
                if (logger is not null)
                {
                    services.RemoveAll<ILogger<BoardRealtimeDispatcher>>();
                    services.AddSingleton(logger);
                }
            });
        }
    }

    private sealed class RecordingLogger : ILogger<BoardRealtimeDispatcher>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(state, exception));
    }
}
