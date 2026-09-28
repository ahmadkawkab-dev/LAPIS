namespace Wukna.IntegrationTests;

using System.Threading.Channels;
using Wukna.Features.Auth;
using Wukna.Features.Board;
using Wukna.Features.Realtime;
using Wukna.Features.Users;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class BoardCursorTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Cursors_are_authorized_connection_scoped_ephemeral_and_activity_neutral()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await postgres.ResetAsync(cancellationToken);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var owner = User("cursor-owner@wukna.test");
        var viewer = User("cursor-viewer@wukna.test");
        var ownerToken = AccessToken(owner, clock);
        var viewerToken = AccessToken(viewer, clock);
        var board = new Board
        {
            Title = "Cursor awareness",
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
            User = viewer,
            Role = BoardRole.Guest,
            CanEdit = false
        });
        await using (var db = postgres.CreateContext())
        {
            db.Users.AddRange(owner, viewer);
            db.Boards.Add(board);
            await db.SaveChangesAsync(cancellationToken);
        }
        DateTimeOffset originalUpdatedAt;
        await using (var baseline = postgres.CreateContext())
        {
            originalUpdatedAt = await baseline.Boards.AsNoTracking()
                .Where(candidate => candidate.Id == board.Id)
                .Select(candidate => candidate.UpdatedAt)
                .SingleAsync(cancellationToken);
        }

        await using var factory = new WuknaWebApplicationFactory(postgres, clock);
        await using var sender = Connection(factory, viewerToken);
        await using var receiver = Connection(factory, ownerToken);
        var moved = Channel.CreateUnbounded<BoardCursorMovedEvent>();
        var stopped = Channel.CreateUnbounded<BoardCursorStoppedEvent>();
        var summary = new TaskCompletionSource<BoardListItemDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.On<BoardCursorMovedEvent>(
            BoardRealtimeEvents.BoardCursorMoved,
            message => moved.Writer.TryWrite(message));
        receiver.On<BoardCursorStoppedEvent>(
            BoardRealtimeEvents.BoardCursorStopped,
            message => stopped.Writer.TryWrite(message));
        receiver.On<BoardListItemDto>(
            BoardRealtimeEvents.BoardSummaryChanged,
            summary.SetResult);
        await sender.StartAsync(cancellationToken);
        await receiver.StartAsync(cancellationToken);
        await sender.InvokeAsync("JoinBoard", board.Id, cancellationToken);
        await receiver.InvokeAsync("JoinBoard", board.Id, cancellationToken);

        await sender.InvokeAsync(
            "MoveBoardCursor",
            new MoveBoardCursorRequest(board.Id, 120, 80, 1),
            cancellationToken);
        var first = await ReadMoved();
        Assert.Equal(viewer.Id, first.UserId);
        Assert.NotEmpty(first.ConnectionId);
        Assert.Equal(120, first.X);
        Assert.Equal(80, first.Y);
        Assert.Equal(clock.GetUtcNow() + BoardCursorRegistry.Lifetime, first.ExpiresAt);

        await sender.InvokeAsync(
            "MoveBoardCursor",
            new MoveBoardCursorRequest(board.Id, 999, 999, 1),
            cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        Assert.False(moved.Reader.TryRead(out _));

        await sender.InvokeAsync("MoveBoardCursor", new MoveBoardCursorRequest(board.Id, -500, -1200, 2), cancellationToken);
        var negative = await ReadMoved();
        Assert.Equal(-500, negative.X);
        Assert.Equal(-1200, negative.Y);

        var invalid = await Assert.ThrowsAsync<HubException>(() => sender.InvokeAsync(
            "MoveBoardCursor",
            new MoveBoardCursorRequest(board.Id, -1, 12, 0),
            cancellationToken));
        Assert.Contains("Invalid", invalid.Message, StringComparison.Ordinal);

        await using var notSubscribed = Connection(factory, viewerToken);
        await notSubscribed.StartAsync(cancellationToken);
        var subscriptionError = await Assert.ThrowsAsync<HubException>(() =>
            notSubscribed.InvokeAsync(
                "MoveBoardCursor",
                new MoveBoardCursorRequest(board.Id, 1, 1, 2),
                cancellationToken));
        Assert.Contains("subscription", subscriptionError.Message, StringComparison.OrdinalIgnoreCase);

        await sender.InvokeAsync(
            "StopBoardCursor",
            new StopBoardCursorRequest(board.Id, 2),
            cancellationToken);
        Assert.Equal(2, (await ReadStopped()).Sequence);

        await sender.InvokeAsync(
            "MoveBoardCursor",
            new MoveBoardCursorRequest(board.Id, 140, 90, 3),
            cancellationToken);
        _ = await ReadMoved();
        await sender.InvokeAsync("LeaveBoard", board.Id, cancellationToken);
        Assert.Equal(3, (await ReadStopped()).Sequence);

        await sender.InvokeAsync("JoinBoard", board.Id, cancellationToken);
        await sender.InvokeAsync(
            "MoveBoardCursor",
            new MoveBoardCursorRequest(board.Id, 150, 100, 4),
            cancellationToken);
        _ = await ReadMoved();
        await sender.StopAsync(cancellationToken);
        Assert.Equal(4, (await ReadStopped()).Sequence);

        await using var revoked = Connection(factory, viewerToken);
        await revoked.StartAsync(cancellationToken);
        await revoked.InvokeAsync("JoinBoard", board.Id, cancellationToken);
        await revoked.InvokeAsync(
            "MoveBoardCursor",
            new MoveBoardCursorRequest(board.Id, 160, 110, 1),
            cancellationToken);
        _ = await ReadMoved();
        var publisher = factory.Services.GetRequiredService<IBoardRealtimePublisher>();
        await publisher.RevokeBoardAccessAsync(board.Id, viewer.Id, cancellationToken);
        Assert.Equal(1, (await ReadStopped()).Sequence);

        await using var expiring = Connection(factory, viewerToken);
        await expiring.StartAsync(cancellationToken);
        await expiring.InvokeAsync("JoinBoard", board.Id, cancellationToken);
        await expiring.InvokeAsync(
            "MoveBoardCursor",
            new MoveBoardCursorRequest(board.Id, 170, 120, 1),
            cancellationToken);
        _ = await ReadMoved();
        clock.Advance(BoardCursorRegistry.Lifetime + TimeSpan.FromMilliseconds(500));
        Assert.Equal(1, (await ReadStopped()).Sequence);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            summary.Task.WaitAsync(TimeSpan.FromMilliseconds(300), cancellationToken));
        await using var verification = postgres.CreateContext();
        Assert.Equal(originalUpdatedAt, await verification.Boards.AsNoTracking()
            .Where(candidate => candidate.Id == board.Id)
            .Select(candidate => candidate.UpdatedAt)
            .SingleAsync(cancellationToken));

        Task<BoardCursorMovedEvent> ReadMoved() =>
            moved.Reader.ReadAsync(cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Task<BoardCursorStoppedEvent> ReadStopped() =>
            stopped.Reader.ReadAsync(cancellationToken).AsTask()
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
