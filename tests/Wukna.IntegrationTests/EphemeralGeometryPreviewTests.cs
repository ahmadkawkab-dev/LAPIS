namespace Wukna.IntegrationTests;

using System.Threading.Channels;
using Wukna.Features.Auth;
using Wukna.Features.Board;
using Wukna.Features.Notes;
using Wukna.Features.Realtime;
using Wukna.Features.Users;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Xunit;

public sealed class EphemeralGeometryPreviewTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Preview_is_authorized_ephemeral_sequenced_and_cleaned_up()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await postgres.ResetAsync(cancellationToken);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var owner = User("preview-owner@wukna.test");
        var editor = User("preview-editor@wukna.test");
        var viewer = User("preview-viewer@wukna.test");
        var board = new Board
        {
            Title = "Ephemeral geometry",
            CreatedAt = clock.GetUtcNow(),
            UpdatedAt = clock.GetUtcNow()
        };
        board.Memberships.Add(new BoardMembership
        {
            User = owner,
            Role = BoardRole.Owner,
            CanEdit = true
        });
        board.Memberships.Add(new BoardMembership
        {
            User = editor,
            Role = BoardRole.Guest,
            CanEdit = true
        });
        board.Memberships.Add(new BoardMembership
        {
            User = viewer,
            Role = BoardRole.Guest,
            CanEdit = false
        });
        var note = new Note
        {
            Board = board,
            Kind = NoteKind.Standalone,
            Title = "Move me",
            PositionX = 10,
            PositionY = 20,
            Width = 240,
            Height = 160
        };
        await using (var db = postgres.CreateContext())
        {
            db.Users.AddRange(owner, editor, viewer);
            db.Notes.Add(note);
            await db.SaveChangesAsync(cancellationToken);
        }
        await using var baseline = postgres.CreateContext();
        var originalUpdatedAt = await baseline.Boards.AsNoTracking()
            .Where(candidate => candidate.Id == board.Id)
            .Select(candidate => candidate.UpdatedAt)
            .SingleAsync(cancellationToken);

        await using var factory = new WuknaWebApplicationFactory(postgres, clock);
        await using var sender = Connection(factory, AccessToken(owner, clock));
        await using var receiver = Connection(factory, AccessToken(editor, clock));
        var previews = Channel.CreateUnbounded<NoteGeometryPreviewEvent>();
        var ended = Channel.CreateUnbounded<NoteGeometryPreviewEndedEvent>();
        var summary = new TaskCompletionSource<BoardListItemDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.On<NoteGeometryPreviewEvent>(
            BoardRealtimeEvents.NoteGeometryPreview,
            message => previews.Writer.TryWrite(message));
        receiver.On<NoteGeometryPreviewEndedEvent>(
            BoardRealtimeEvents.NoteGeometryPreviewEnded,
            message => ended.Writer.TryWrite(message));
        receiver.On<BoardListItemDto>(
            BoardRealtimeEvents.BoardSummaryChanged,
            summary.SetResult);
        await sender.StartAsync(cancellationToken);
        await receiver.StartAsync(cancellationToken);
        await sender.InvokeAsync("JoinBoard", board.Id, cancellationToken);
        await receiver.InvokeAsync("JoinBoard", board.Id, cancellationToken);

        var drag = new NoteGeometryPreviewRequest(
            board.Id,
            note.Id,
            NoteGeometryOperation.Drag,
            -500,
            -1200,
            null,
            null,
            note.Version,
            1);
        await sender.InvokeAsync("PreviewNoteGeometry", drag, cancellationToken);
        var received = await ReadPreview();
        Assert.Equal(owner.Id, received.UserId);
        Assert.NotEmpty(received.ConnectionId);
        Assert.Equal(NoteGeometryOperation.Drag, received.Operation);
        Assert.Equal(-500, received.X);
        Assert.Equal(-1200, received.Y);
        Assert.Equal(note.Version, received.BaseVersion);
        Assert.Equal(clock.GetUtcNow(), received.SentAt);

        await sender.InvokeAsync("PreviewNoteGeometry", drag, cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        Assert.False(previews.Reader.TryRead(out _));

        var resize = drag with
        {
            Operation = NoteGeometryOperation.Resize,
            X = null,
            Y = null,
            Width = 320,
            Height = 210,
            Sequence = 2
        };
        await sender.InvokeAsync("PreviewNoteGeometry", resize, cancellationToken);
        Assert.Equal(NoteGeometryOperation.Resize,
            (await ReadPreview()).Operation);
        await sender.InvokeAsync(
            "EndNoteGeometryPreview",
            new EndNoteGeometryPreviewRequest(board.Id, note.Id, 2),
            cancellationToken);
        Assert.Equal(2, (await ReadEnded()).Sequence);

        var disconnectPreview = drag with { Sequence = 3, X = 180, Y = 200 };
        await sender.InvokeAsync("PreviewNoteGeometry", disconnectPreview, cancellationToken);
        _ = await ReadPreview();
        await using var idleMember = Connection(factory, AccessToken(viewer, clock));
        var idleEnded = new TaskCompletionSource<NoteGeometryPreviewEndedEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        idleMember.On<NoteGeometryPreviewEndedEvent>(
            BoardRealtimeEvents.NoteGeometryPreviewEnded,
            idleEnded.SetResult);
        await idleMember.StartAsync(cancellationToken);
        await sender.StopAsync(cancellationToken);
        Assert.Equal(3, (await ReadEnded()).Sequence);
        await Assert.ThrowsAsync<TimeoutException>(() =>
            idleEnded.Task.WaitAsync(TimeSpan.FromMilliseconds(300), cancellationToken));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            summary.Task.WaitAsync(TimeSpan.FromMilliseconds(300), cancellationToken));
        await using (var verification = postgres.CreateContext())
        {
            var persisted = await verification.Notes.AsNoTracking().SingleAsync(
                candidate => candidate.Id == note.Id, cancellationToken);
            Assert.Equal(10, persisted.PositionX);
            Assert.Equal(20, persisted.PositionY);
            Assert.Equal(240, persisted.Width);
            Assert.Equal(160, persisted.Height);
            Assert.Equal(originalUpdatedAt, await verification.Boards.AsNoTracking()
                .Where(candidate => candidate.Id == board.Id)
                .Select(candidate => candidate.UpdatedAt)
                .SingleAsync(cancellationToken));
        }

        await using var notSubscribed = Connection(factory, AccessToken(owner, clock));
        await notSubscribed.StartAsync(cancellationToken);
        var subscriptionError = await Assert.ThrowsAsync<HubException>(() =>
            notSubscribed.InvokeAsync("PreviewNoteGeometry", drag with { Sequence = 4 },
                cancellationToken));
        Assert.Contains("subscription", subscriptionError.Message, StringComparison.OrdinalIgnoreCase);

        await using var readOnly = Connection(factory, AccessToken(viewer, clock));
        await readOnly.StartAsync(cancellationToken);
        await readOnly.InvokeAsync("JoinBoard", board.Id, cancellationToken);
        var permissionError = await Assert.ThrowsAsync<HubException>(() =>
            readOnly.InvokeAsync("PreviewNoteGeometry", drag with { Sequence = 5 },
                cancellationToken));
        Assert.Contains("Forbidden", permissionError.Message, StringComparison.Ordinal);

        Task<NoteGeometryPreviewEvent> ReadPreview() =>
            previews.Reader.ReadAsync(cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        Task<NoteGeometryPreviewEndedEvent> ReadEnded() =>
            ended.Reader.ReadAsync(cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
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
}
