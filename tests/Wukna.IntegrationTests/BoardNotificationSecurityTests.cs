namespace Wukna.IntegrationTests;

using System.Data.Common;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Wukna.Features.Board;
using Wukna.Features.Chat;
using Wukna.Features.Notifications;
using Wukna.Features.Realtime;
using Wukna.Shared.Data.AppDbContext;
using Xunit;

public sealed class BoardNotificationSecurityTests(PostgresFixture postgres)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("payload-read")]
    [InlineData("send-initiation")]
    [InlineData("validation-failure")]
    public async Task Board_notification_delivery_fails_closed_when_visibility_becomes_stale_or_validation_fails(string failure)
    {
        var seed = await ChatControlTestSupport.Seed(postgres, Ct);
        var notification = await SeedNotification(seed.Board.Id, seed.Guest.Id, seed.Clock);
        var gate = new DeliveryGate(failure);
        var transport = new BoardTransportSpy();
        await using var factory = new BoardSecurityApplicationFactory(postgres, seed.Clock, gate)
        {
            ConfigureOverrides = services =>
            {
                services.AddSingleton<IHubContext<BoardHub>>(transport);
                services.AddSingleton<INotificationPushSender>(gate);
            }
        };
        var registry = factory.Services.GetRequiredService<IBoardConnectionRegistry>();
        var worker = factory.Services.GetRequiredService<NotificationDispatcher>();
        var delivery = worker.ProcessAsync(Ct);
        if (failure != "validation-failure")
        {
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
                await using var lifecycle = await registry.EnterLifecycleAsync(seed.Board.Id, Ct);
                await using var db = postgres.CreateContext(); await using var transaction = await db.Database.BeginTransactionAsync(Ct);
                Assert.True(await BoardMembershipLocks.LockBoardAsync(db, seed.Board.Id, Ct));
                var change = registry.BeginAccessChange(seed.Board.Id, [seed.Guest.Id]);
                db.BoardMemberships.Remove((await db.BoardMemberships.FindAsync([seed.Board.Id, seed.Guest.Id], Ct))!);
                await db.SaveChangesAsync(Ct); await transaction.CommitAsync(Ct);
                registry.CompleteAccessChange(change, true);
            }
            finally { gate.Release.TrySetResult(); }
        }
        await delivery;
        Assert.DoesNotContain(transport.Deliveries, item => item.EventName == "NotificationChanged");
        await using var check = postgres.CreateContext();
        var retained = await check.NotificationWork.SingleAsync(item => item.NotificationId == notification.Id, Ct);
        Assert.Equal(failure != "payload-read", retained.ProcessedAt is null);
        if (failure == "validation-failure") Assert.True(gate.ValidationFailed);
    }

    [Fact]
    public async Task Authorized_board_notifications_reach_user_addressing_without_a_board_join_and_personal_notifications_keep_their_contract()
    {
        var seed = await ChatControlTestSupport.Seed(postgres, Ct);
        var board = await SeedNotification(seed.Board.Id, seed.Guest.Id, seed.Clock);
        var personal = await SeedNotification(null, seed.Guest.Id, seed.Clock);
        var transport = new BoardTransportSpy();
        await using var factory = new BoardSecurityApplicationFactory(postgres, seed.Clock)
        {
            ConfigureOverrides = services =>
            {
                services.AddSingleton<IHubContext<BoardHub>>(transport);
                services.AddSingleton<INotificationPushSender>(new DeliveryGate("none"));
            }
        };
        Assert.Empty(factory.Services.GetRequiredService<IBoardConnectionRegistry>().GetConnections(seed.Board.Id));
        await factory.Services.GetRequiredService<NotificationDispatcher>().ProcessAsync(Ct);
        var deliveries = transport.Deliveries.Where(item => item.EventName == "NotificationChanged").ToArray();
        Assert.Equal(2, deliveries.Length);
        Assert.All(deliveries, item => { Assert.Equal("User", item.Kind); Assert.Equal([seed.Guest.Id.ToString()], item.Targets); });
        Assert.Contains(deliveries, item => Assert.IsType<NotificationChangedEvent>(Assert.Single(item.Args)).Notification.Id == board.Id);
        Assert.Contains(deliveries, item => Assert.IsType<NotificationChangedEvent>(Assert.Single(item.Args)).Notification.Id == personal.Id);
    }

    private async Task<Notification> SeedNotification(Guid? boardId, Guid user, TimeProvider clock)
    {
        await using var db = postgres.CreateContext();
        Guid? instance = null;
        if (boardId is Guid board)
        {
            var state = new BoardMemberChatState { BoardId = board, UserId = user };
            db.BoardMemberChatStates.Add(state); instance = state.MembershipInstanceId;
        }
        var notification = new Notification
        {
            UserId = user, BoardId = boardId, MembershipInstanceId = instance,
            Type = boardId is null ? NotificationType.TaskActivity : NotificationType.SharedBoardActivity,
            Title = boardId is null ? "Personal task" : "Sensitive board title",
            IssuedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow()
        };
        db.Notifications.Add(notification); NotificationSources.Deliver(db, notification, clock.GetUtcNow());
        await db.SaveChangesAsync(Ct); return notification;
    }

    private sealed class DeliveryGate(string failure) : DbCommandInterceptor, INotificationPushSender
    {
        private int paused;
        public bool ValidationFailed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data,
            DbDataReader result, CancellationToken ct = default)
        {
            if (failure == "payload-read" && command.CommandText.Contains("FROM notifications AS", StringComparison.Ordinal) &&
                !command.CommandText.StartsWith("SELECT EXISTS", StringComparison.Ordinal) && Interlocked.Exchange(ref paused, 1) == 0)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
            return result;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (failure == "validation-failure" && command.CommandText.StartsWith("SELECT EXISTS", StringComparison.Ordinal) &&
                command.CommandText.Contains("FROM notifications AS", StringComparison.Ordinal))
            { ValidationFailed = true; throw new IOException("test-only board notification validation failure"); }
            return ValueTask.FromResult(result);
        }
        public async Task QueueAsync(WuknaDbContext db, Notification notification, DateTimeOffset now, CancellationToken ct)
        {
            if (failure == "send-initiation") { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
        }
        public Task SendAsync(WuknaDbContext db, NotificationWork work, Notification notification, NotificationPreference preference, CancellationToken ct) => Task.CompletedTask;
    }
}
