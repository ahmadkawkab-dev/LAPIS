namespace Wukna.IntegrationTests;

using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wukna.Features.Realtime;
using Wukna.Shared.Data.AppDbContext;
using Xunit;

public sealed class BoardRealtimePerformanceTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(2, 0)]
    [InlineData(10, 30)]
    public async Task Compare_native_groups_registry_coordination_and_per_publication_database_locking_locally(int boardConnections, int unrelatedConnections)
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await ChatControlTestSupport.Seed(postgres, ct); var sql = new BoardSqlCounter();
        await using var factory = new BoardSecurityApplicationFactory(postgres, seed.Clock, sql);
        using var owner = factory.Client(seed.Owner);
        var sockets = new List<HubConnection>();
        var observations = new List<BoardSocketObservation>();
        var received = new int[boardConnections + unrelatedConnections];
        try
        {
            for (var index = 0; index < received.Length; index++)
            {
                var socket = factory.Connection(owner, index == 0 ? seed.Owner : index < boardConnections ? seed.Peer : seed.Outsider);
                var receiverIndex = index;
                socket.On<BoardCursorMovedEvent>("SecurityPerformanceProbe", _ => Interlocked.Increment(ref received[receiverIndex]));
                observations.Add(new BoardSocketObservation(socket)); sockets.Add(socket);
                await socket.StartAsync(ct);
                if (index < boardConnections) await socket.InvokeAsync("JoinBoard", seed.Board.Id, ct);
            }
        var first = sockets[0];
        var hub = factory.Services.GetRequiredService<IHubContext<BoardHub>>();
        var publisher = factory.Services.GetRequiredService<IBoardRealtimePublisher>();
        var registry = factory.Services.GetRequiredService<IBoardConnectionRegistry>();
        var message = new BoardCursorMovedEvent(seed.Board.Id, seed.Owner.Id, first.ConnectionId!, 100, 200, 1, seed.Clock.GetUtcNow().AddSeconds(10));
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<WuknaDbContext>();
        Func<Task> native = () => hub.Clients.Group(BoardRealtimeGroups.ForBoard(seed.Board.Id)).SendAsync("SecurityPerformanceProbe", message, ct);
        Func<Task> coordinated = () => publisher.PublishBoardAsync(seed.Board.Id, "SecurityPerformanceProbe", message, ct);
        async Task DatabaseLocked()
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var members = await db.BoardMemberships.FromSqlInterpolated($"SELECT * FROM board_memberships WHERE board_id = {seed.Board.Id} ORDER BY user_id FOR SHARE")
                .AsNoTracking().ToArrayAsync(ct);
            Assert.Equal(3, members.Length);
            await hub.Clients.Clients(registry.GetConnections(seed.Board.Id).ToArray()).SendAsync("SecurityPerformanceProbe", message, ct);
            await transaction.CommitAsync(ct);
        }
        foreach (var action in new Func<Task>[] { native, coordinated, DatabaseLocked }) for (var index = 0; index < 20; index++) await action();
        var results = new List<Measurement>();
        for (var round = 0; round < 3; round++)
        {
            results.Add(await Measure("native-group", native, 500, round));
            var revised = await Measure("registry-coordination", coordinated, 500, round); results.Add(revised); Assert.Equal(0, revised.SqlCommands);
            var locked = await Measure("per-publication-db-lock", DatabaseLocked, 200, round); results.Add(locked); Assert.Equal(200, locked.SqlCommands);
        }
        var path = Path.Combine(Path.GetTempPath(), $"wukna-board-realtime-performance-{boardConnections}-{unrelatedConnections}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            Environment = "Local Release build; Production Kestrel; real WebSocket connections; PostgreSQL test container",
            BoardConnections = boardConnections, UnrelatedConnections = unrelatedConnections,
            Limitations = "Server send completion latency, not browser end-to-end latency. Allocations are process-wide and include client work. No group-removal failure injection.",
            Results = results
        }, new JsonSerializerOptions { WriteIndented = true }), ct);

        foreach (var observation in observations) await observation.FenceAsync(factory, ct);
        Assert.All(received.Take(boardConnections), count => Assert.Equal(3660, count));
        Assert.All(received.Skip(boardConnections), count => Assert.Equal(0, count));
        }
        finally { foreach (var socket in sockets) await socket.DisposeAsync(); }

        async Task<Measurement> Measure(string name, Func<Task> action, int count, int round)
        {
            sql.Reset(); var allocated = GC.GetTotalAllocatedBytes(precise: true); var samples = new double[count];
            var total = Stopwatch.StartNew();
            for (var index = 0; index < count; index++) { var start = Stopwatch.GetTimestamp(); await action(); samples[index] = Stopwatch.GetElapsedTime(start).TotalMicroseconds; }
            total.Stop(); Array.Sort(samples);
            return new(name, round, count, sql.Count, total.Elapsed.TotalMilliseconds, samples[count / 2], samples[(int)(count * .95)],
                samples[(int)(count * .99)], (GC.GetTotalAllocatedBytes(precise: true) - allocated) / count);
        }
    }
    private sealed record Measurement(string Path, int Round, int Publications, int SqlCommands,
        double TotalMilliseconds, double MedianMicroseconds, double P95Microseconds, double P99Microseconds, long ProcessAllocatedBytesPerPublication);
}
