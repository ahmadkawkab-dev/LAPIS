namespace Wukna.IntegrationTests;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Wukna.Features.Board;
using Wukna.Features.Chat;
using Wukna.Features.Users;
using Wukna.Shared.Data.AppDbContext;
using Xunit;

public sealed class ChatSchemaTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Migration_backfills_existing_boards_and_members_without_changing_permissions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = postgres.CreateContext();
        await db.Database.EnsureDeletedAsync(ct);
        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync("20261001165613_AddTaskRemindersAndNotifications", ct);
        var (board, owner, guest) = BoardWithMembers();
        db.Boards.Add(board);
        await db.SaveChangesAsync(ct);

        await migrator.MigrateAsync(cancellationToken: ct);
        var settings = await db.BoardChatSettings.SingleAsync(ct);
        Assert.Equal(board.Id, settings.BoardId);
        Assert.Equal(0, settings.SlowModeSeconds);
        Assert.Equal(1, settings.SettingsRevision);
        Assert.Equal(0, settings.LastMessageSequence);
        var states = await db.BoardMemberChatStates.ToListAsync(ct);
        Assert.Equal(2, states.Count);
        Assert.Equal(2, states.Select(state => state.MembershipInstanceId).Distinct().Count());
        Assert.All(states, state =>
        {
            Assert.NotEqual(Guid.Empty, state.MembershipInstanceId);
            Assert.False(state.IsMuted);
            Assert.Null(state.NextSendAllowedAt);
            Assert.Equal(0, state.LastReadSequence);
        });
        Assert.True(await db.BoardMemberships.AnyAsync(member => member.UserId == owner.Id &&
            member.Role == BoardRole.Owner && member.CanEdit, ct));
        Assert.True(await db.BoardMemberships.AnyAsync(member => member.UserId == guest.Id &&
            member.Role == BoardRole.Guest && !member.CanEdit, ct));
    }

    [Fact]
    public async Task Idempotency_is_scoped_to_board_and_sender_and_history_sequence_is_unique()
    {
        var ct = TestContext.Current.CancellationToken;
        var (board, owner, guest) = await SeedAsync(ct);
        var operation = Guid.NewGuid();
        await using (var db = postgres.CreateContext())
        {
            db.ChatMessages.Add(Message(board.Id, owner.Id, 1, operation));
            await db.SaveChangesAsync(ct);
        }
        await AssertRejectedAsync(db => db.ChatMessages.Add(Message(board.Id, owner.Id, 2, operation)),
            PostgresErrorCodes.UniqueViolation, "ix_chat_messages_board_id_sender_user_id_client_message_id", ct);
        await AssertRejectedAsync(db => db.ChatMessages.Add(Message(board.Id, guest.Id, 1)),
            PostgresErrorCodes.UniqueViolation, "ix_chat_messages_board_id_sequence", ct);

        await using var allowed = postgres.CreateContext();
        var otherBoard = new Board { Title = "Other board" };
        allowed.Boards.Add(otherBoard);
        allowed.ChatMessages.Add(Message(board.Id, guest.Id, 2, operation));
        allowed.ChatMessages.Add(Message(otherBoard.Id, owner.Id, 1, operation));
        await allowed.SaveChangesAsync(ct);
        Assert.Equal(3, await allowed.ChatMessages.CountAsync(ct));
    }

    [Fact]
    public async Task Removal_resets_member_state_but_preserves_history_and_restricts_sender_deletion()
    {
        var ct = TestContext.Current.CancellationToken;
        var (board, _, guest) = await SeedAsync(ct);
        var oldInstance = Guid.NewGuid();
        await using (var db = postgres.CreateContext())
        {
            db.BoardMemberChatStates.Add(new BoardMemberChatState
            {
                BoardId = board.Id, UserId = guest.Id, MembershipInstanceId = oldInstance,
                IsMuted = true, ModerationRevision = 1, LastReadSequence = 1,
                NextSendAllowedAt = DateTimeOffset.UtcNow.AddMinutes(1), CooldownSettingsRevision = 1
            });
            db.ChatMessages.Add(Message(board.Id, guest.Id, 1));
            await db.SaveChangesAsync(ct);
            await db.BoardMemberships.Where(member => member.BoardId == board.Id && member.UserId == guest.Id)
                .ExecuteDeleteAsync(ct);
            Assert.False(await db.BoardMemberChatStates.AnyAsync(ct));
            Assert.Equal(1, await db.ChatMessages.CountAsync(ct));
        }
        await using (var db = postgres.CreateContext())
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                db.Users.Where(user => user.Id == guest.Id).ExecuteDeleteAsync(ct));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        }
        await using var reinvite = postgres.CreateContext();
        reinvite.BoardMemberships.Add(new BoardMembership { BoardId = board.Id, UserId = guest.Id });
        var fresh = new BoardMemberChatState { BoardId = board.Id, UserId = guest.Id };
        reinvite.BoardMemberChatStates.Add(fresh);
        await reinvite.SaveChangesAsync(ct);
        reinvite.ChangeTracker.Clear();
        var restored = await reinvite.BoardMemberChatStates.SingleAsync(ct);
        Assert.NotEqual(oldInstance, restored.MembershipInstanceId);
        Assert.False(restored.IsMuted);
        Assert.Null(restored.NextSendAllowedAt);
        Assert.Equal(0, restored.LastReadSequence);
    }

    [Fact]
    public async Task Board_cascade_removes_chat_data_but_retains_outbox_and_blob_cleanup_work()
    {
        var ct = TestContext.Current.CancellationToken;
        var (board, owner, guest) = await SeedAsync(ct);
        await using var db = postgres.CreateContext();
        var attachmentMessage = Message(board.Id, owner.Id, 1);
        attachmentMessage.Type = ChatMessageType.Attachment;
        attachmentMessage.Attachment = Attachment();
        var taskMessage = Message(board.Id, guest.Id, 2);
        taskMessage.Type = ChatMessageType.ScheduledTask;
        taskMessage.Body = null;
        taskMessage.ScheduledTask = Task();
        db.ChatMessages.AddRange(attachmentMessage, taskMessage);
        db.BoardChatSettings.Add(new BoardChatSettings { BoardId = board.Id, LastMessageSequence = 2 });
        db.BoardMemberChatStates.Add(new BoardMemberChatState { BoardId = board.Id, UserId = guest.Id });
        db.ChatOutboxEvents.Add(new ChatOutboxEvent { BoardId = board.Id, Kind = ChatOutboxEventKind.AccessRevoked });
        db.ChatBlobWork.Add(BlobWork(board.Id));
        var other = new Board { Title = "Preserved" };
        db.Boards.Add(other);
        db.ChatMessages.Add(Message(other.Id, owner.Id, 1));
        await db.SaveChangesAsync(ct);

        await db.Boards.Where(item => item.Id == board.Id).ExecuteDeleteAsync(ct);
        Assert.False(await db.BoardChatSettings.AnyAsync(ct));
        Assert.False(await db.BoardMemberChatStates.AnyAsync(ct));
        Assert.False(await db.ChatAttachments.AnyAsync(ct));
        Assert.False(await db.ScheduledChatTasks.AnyAsync(ct));
        Assert.Equal(other.Id, (await db.ChatMessages.SingleAsync(ct)).BoardId);
        Assert.Equal(board.Id, (await db.ChatOutboxEvents.SingleAsync(ct)).BoardId);
        Assert.Equal(board.Id, (await db.ChatBlobWork.SingleAsync(ct)).BoardId);
    }

    [Fact]
    public async Task Rolling_back_domain_transaction_also_rolls_back_outbox_and_sequence()
    {
        var ct = TestContext.Current.CancellationToken;
        var (board, owner, _) = await SeedAsync(ct);
        await using var db = postgres.CreateContext();
        db.BoardChatSettings.Add(new BoardChatSettings { BoardId = board.Id });
        await db.SaveChangesAsync(ct);
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var message = Message(board.Id, owner.Id, 1);
            db.ChatMessages.Add(message);
            db.ChatOutboxEvents.Add(new ChatOutboxEvent
            {
                BoardId = board.Id, Kind = ChatOutboxEventKind.MessageCreated,
                MessageId = message.Id, MessageSequence = message.Sequence
            });
            (await db.BoardChatSettings.SingleAsync(ct)).LastMessageSequence = 1;
            await db.SaveChangesAsync(ct);
            await transaction.RollbackAsync(ct);
        }
        db.ChangeTracker.Clear();
        Assert.False(await db.ChatMessages.AnyAsync(ct));
        Assert.False(await db.ChatOutboxEvents.AnyAsync(ct));
        Assert.Equal(0, (await db.BoardChatSettings.SingleAsync(ct)).LastMessageSequence);
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("fingerprint")]
    [InlineData("client_id")]
    [InlineData("blank_text")]
    [InlineData("cooldown")]
    [InlineData("read_cursor")]
    [InlineData("attachment_scan")]
    [InlineData("attachment_lease")]
    [InlineData("task_end")]
    [InlineData("outbox_payload")]
    [InlineData("blob_reservation")]
    public async Task Database_rejects_invalid_chat_records(string invalidCase)
    {
        var ct = TestContext.Current.CancellationToken;
        var (board, owner, _) = await SeedAsync(ct);
        await using var db = postgres.CreateContext();
        var message = Message(board.Id, owner.Id, 1);
        switch (invalidCase)
        {
            case "sequence": message.Sequence = 0; db.Add(message); break;
            case "fingerprint": message.RequestFingerprint = "invalid"; db.Add(message); break;
            case "client_id": message.ClientMessageId = Guid.Empty; db.Add(message); break;
            case "blank_text": message.Body = "   "; db.Add(message); break;
            case "cooldown": db.Add(new BoardChatSettings { BoardId = board.Id, SlowModeSeconds = -1 }); break;
            case "read_cursor": db.Add(new BoardMemberChatState { BoardId = board.Id, UserId = owner.Id, LastReadSequence = -1 }); break;
            case "attachment_scan":
            case "attachment_lease":
                message.Type = ChatMessageType.Attachment;
                message.Attachment = Attachment();
                message.Attachment.ScanStatus = invalidCase == "attachment_scan"
                    ? ChatAttachmentScanStatus.Available : ChatAttachmentScanStatus.Scanning;
                db.Add(message);
                break;
            case "task_end":
                message.Type = ChatMessageType.ScheduledTask;
                message.Body = null;
                message.ScheduledTask = Task();
                message.ScheduledTask.EndsAtUtc = message.ScheduledTask.StartsAtUtc.AddMinutes(-1);
                db.Add(message);
                break;
            case "outbox_payload":
                db.Add(new ChatOutboxEvent { BoardId = board.Id, Kind = ChatOutboxEventKind.MessageCreated, MessageId = message.Id });
                break;
            case "blob_reservation":
                var work = BlobWork(board.Id);
                work.Purpose = ChatBlobWorkPurpose.UploadReservation;
                db.Add(work);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(invalidCase));
        }
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    private async Task AssertRejectedAsync(Action<WuknaDbContext> add, string sqlState, string constraint, CancellationToken ct)
    {
        await using var db = postgres.CreateContext();
        add(db);
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
        var postgresError = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(sqlState, postgresError.SqlState);
        Assert.Equal(constraint, postgresError.ConstraintName);
    }

    private async Task<(Board Board, User Owner, User Guest)> SeedAsync(CancellationToken ct)
    {
        await postgres.ResetAsync(ct);
        var seed = BoardWithMembers();
        await using var db = postgres.CreateContext();
        db.Boards.Add(seed.Board);
        await db.SaveChangesAsync(ct);
        return seed;
    }

    private static (Board Board, User Owner, User Guest) BoardWithMembers()
    {
        var owner = new User { Email = "chat-owner@wukna.test", NormalizedEmail = "CHAT-OWNER@WUKNA.TEST" };
        var guest = new User { Email = "chat-guest@wukna.test", NormalizedEmail = "CHAT-GUEST@WUKNA.TEST" };
        var board = new Board { Title = "Chat schema" };
        board.Memberships.Add(new BoardMembership { User = owner, Role = BoardRole.Owner, CanEdit = true });
        board.Memberships.Add(new BoardMembership { User = guest, Role = BoardRole.Guest, CanEdit = false });
        return (board, owner, guest);
    }

    private static ChatMessage Message(Guid boardId, Guid senderId, long sequence, Guid? operation = null) => new()
    {
        BoardId = boardId, SenderUserId = senderId, Sequence = sequence,
        Body = "Persisted text", ClientMessageId = operation ?? Guid.NewGuid(),
        RequestFingerprint = new string('a', 64)
    };

    private static ChatAttachment Attachment() => new()
    {
        OriginalStorageKey = "original", StorageKey = "normalized", PreviewStorageKey = "preview",
        OriginalFileName = "image.png", InputByteSize = 100, ByteSize = 80,
        PreviewByteSize = 20, StoredByteSize = 200, Width = 10, Height = 10
    };

    private static ScheduledChatTask Task() => new()
    {
        Title = "Deploy", StartsAtUtc = new DateTimeOffset(2026, 10, 9, 11, 0, 0, TimeSpan.Zero),
        TimeZoneId = "Asia/Beirut", OriginalOffsetMinutes = 180
    };

    private static ChatBlobWork BlobWork(Guid boardId) => new()
    {
        BoardId = boardId, Purpose = ChatBlobWorkPurpose.Delete,
        OriginalStorageKey = "original", StorageKey = "normalized", PreviewStorageKey = "preview",
        DueAt = DateTimeOffset.UtcNow
    };
}
