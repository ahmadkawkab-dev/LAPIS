namespace Wukna.Features.Board;

using System.IdentityModel.Tokens.Jwt;
using Wukna.Features.Realtime;
using Wukna.Features.Notes;
using Wukna.Features.Users;
using Wukna.Features.Chat;
using Wukna.Shared.Data.AppDbContext;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Wukna.Features.Notifications;
using Microsoft.Extensions.Options;

public sealed record CreateBoardRequest(string Title);
public sealed record RenameBoardRequest(string Title);
public sealed record SetGuestAccessRequest(string Email, bool CanEdit);
public sealed record SetMemberPermissionRequest(bool CanEdit);
public sealed record BoardMemberDto(
    Guid UserId, string Email, string Username, string? DisplayName,
    string? ProfileImageUrl, string? ProfileImageVersion, BoardRole Role, bool CanEdit);

public static class BoardEndpoints
{
    private const int MaxOwnedBoards = 5;

    public static IEndpointRouteBuilder MapBoardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/boards").RequireAuthorization();

        group.MapGet("/", async (HttpContext context, BoardSummaryReader reader,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            return Results.Ok(await reader.ListAsync(userId, cancellationToken));
        });

        group.MapGet("/{boardId:guid}", async (
            Guid boardId,
            HttpContext context,
            WuknaDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();

            var board = await db.BoardMemberships.AsNoTracking()
                .Where(membership => membership.BoardId == boardId && membership.UserId == userId)
                .Select(membership => new BoardDetailDto(
                    membership.BoardId,
                    membership.Board.Title,
                    membership.Board.CreatedAt,
                    membership.Board.UpdatedAt,
                    membership.Role,
                    membership.Role == BoardRole.Owner || membership.CanEdit))
                .SingleOrDefaultAsync(cancellationToken);
            return board is null ? Results.NotFound() : Results.Ok(board);
        });

        group.MapPost("/", async (
            CreateBoardRequest request,
            HttpContext context,
            WuknaDbContext db,
            BoardActivity activity,
            BoardRealtimeDispatcher realtime,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var title = request.Title?.Trim();
            if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
                return Results.BadRequest(new { error = "Title must contain 1 to 200 characters." });

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            // Serialize board creation for this user, including requests handled by other servers.
            var users = await db.Users
                .FromSqlInterpolated($"SELECT * FROM asp_net_users WHERE id = {userId} FOR UPDATE")
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            if (users.Count == 0) return Results.Unauthorized();

            var ownedCount = await db.BoardMemberships.CountAsync(
                membership => membership.UserId == userId && membership.Role == BoardRole.Owner,
                cancellationToken);
            if (ownedCount >= MaxOwnedBoards)
                return Results.Conflict(new { error = "board_limit_reached" });

            var board = new Board { Title = title };
            activity.Initialize(board);
            board.Memberships.Add(new BoardMembership
            {
                UserId = userId,
                Role = BoardRole.Owner,
                CanEdit = true
            });
            db.Boards.Add(board);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var response = new BoardDetailDto(
                board.Id, board.Title, board.CreatedAt, board.UpdatedAt, BoardRole.Owner, true);
            await realtime.BoardCreatedAsync(board.Id);
            return Results.Created($"/api/boards/{board.Id}", response);
        });

        group.MapPatch("/{boardId:guid}", async (
            Guid boardId,
            RenameBoardRequest request,
            HttpContext context,
            WuknaDbContext db,
            BoardActivity activity,
            BoardRealtimeDispatcher realtime,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var title = request.Title?.Trim();
            if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
                return Results.BadRequest(new { error = "Title must contain 1 to 200 characters." });

            var board = await db.Boards
                .Where(candidate => candidate.Id == boardId && candidate.Memberships.Any(
                    membership => membership.UserId == userId &&
                                  membership.Role == BoardRole.Owner))
                .SingleOrDefaultAsync(cancellationToken);
            if (board is null) return Results.NotFound();

            if (!string.Equals(board.Title, title, StringComparison.Ordinal))
            {
                board.Title = title;
                activity.MarkUpdated(db, boardId);
                await db.SaveChangesAsync(cancellationToken);
                await realtime.BoardUpdatedAsync(new BoardUpdatedEvent(
                    board.Id, board.Title, board.UpdatedAt));
            }

            return Results.Ok(new BoardDetailDto(
                board.Id, board.Title, board.CreatedAt, board.UpdatedAt,
                BoardRole.Owner, true));
        });

        group.MapDelete("/{boardId:guid}", async (
            Guid boardId, HttpContext context, WuknaDbContext db,
            BoardRealtimeDispatcher realtime, ChatRevocation chatRevocation, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (!await IsOwnerAsync(db, boardId, userId, cancellationToken)) return Results.NotFound();

            var memberIds = await db.BoardMemberships.AsNoTracking()
                .Where(membership => membership.BoardId == boardId)
                .Select(membership => membership.UserId)
                .ToArrayAsync(cancellationToken);

            // Checklist parents use a restrictive FK, so remove dependent rows in order.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            // Serialize deletion with bounded upload/promotion before inspecting blob keys.
            var lockedBoards = await db.Boards.FromSqlInterpolated($"SELECT * FROM boards WHERE id = {boardId} FOR UPDATE")
                .AsNoTracking().ToListAsync(cancellationToken);
            if (lockedBoards.Count == 0 || !await IsOwnerAsync(db, boardId, userId, cancellationToken)) return Results.NotFound();
            await ChatAttachmentJobs.QueueBoardDeletionAsync(db, boardId, clock.GetUtcNow(), cancellationToken);
            var chatRevoked = chatRevocation.Record(boardId);
            await db.SaveChangesAsync(cancellationToken);
            await db.NoteConnections.Where(connection => connection.BoardId == boardId)
                .ExecuteDeleteAsync(cancellationToken);
            await db.Notes.Where(note => note.BoardId == boardId && note.Kind == NoteKind.ChecklistItem)
                .ExecuteDeleteAsync(cancellationToken);
            await db.Notes.Where(note => note.BoardId == boardId)
                .ExecuteDeleteAsync(cancellationToken);
            await db.Boards.Where(board => board.Id == boardId)
                .ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await chatRevocation.NotifyAsync(chatRevoked);
            await realtime.BoardDeletedAsync(boardId, memberIds);
            return Results.NoContent();
        });

        group.MapGet("/{boardId:guid}/members", async (
            Guid boardId, HttpContext context, WuknaDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (!await db.BoardMemberships.AnyAsync(m => m.BoardId == boardId &&
                    m.UserId == userId, cancellationToken)) return Results.NotFound();

            var members = await db.BoardMemberships.AsNoTracking()
                .Where(m => m.BoardId == boardId)
                .OrderByDescending(m => m.Role).ThenBy(m => m.User.Email)
                .Select(m => new BoardMemberDto(m.UserId, m.User.Email ?? "", m.User.Username,
                    m.User.DisplayName, Wukna.Features.Users.ProfileImageUrls.For(m.User.ProfileImageKey, m.User.ProfileImageVersion),
                    m.User.ProfileImageVersion, m.Role,
                    m.Role == BoardRole.Owner || m.CanEdit))
                .ToListAsync(cancellationToken);
            return Results.Ok(members);
        });

        group.MapPut("/{boardId:guid}/guests", async (
            Guid boardId,
            SetGuestAccessRequest request,
            HttpContext context,
            WuknaDbContext db,
            UserManager<User> userManager,
            BoardActivity activity,
            BoardRealtimeDispatcher realtime,
            IOptionsMonitor<BoardOptions> options, TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var callerRole = await db.BoardMemberships.AsNoTracking()
                .Where(membership => membership.BoardId == boardId && membership.UserId == userId)
                .Select(membership => (BoardRole?)membership.Role)
                .SingleOrDefaultAsync(cancellationToken);
            if (callerRole is null) return Results.NotFound();
            if (callerRole != BoardRole.Owner) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(request.Email))
                return Results.BadRequest(new { error = "Guest email is required." });

            var guest = await userManager.FindByEmailAsync(request.Email.Trim());
            if (guest is null) return Results.NotFound();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            if (!await BoardMembershipLocks.LockBoardAsync(db, boardId, cancellationToken)) return Results.NotFound();
            if ((await ChatAccess.LockMembershipAsync(db, boardId, userId, cancellationToken))?.Role != BoardRole.Owner)
                return Results.NotFound();

            var membership = await db.BoardMemberships.FindAsync(
                [boardId, guest.Id], cancellationToken);
            if (membership?.Role == BoardRole.Owner)
                return Results.Conflict(new { error = "The board owner cannot become a guest." });

            var changed = membership is null || membership.CanEdit != request.CanEdit;
            var downgraded = membership?.CanEdit == true && !request.CanEdit;
            if (membership is null)
            {
                var maxGuests = options.CurrentValue.MaxGuests;
                var guestCount = await db.BoardMemberships.CountAsync(member =>
                    member.BoardId == boardId && member.Role == BoardRole.Guest, cancellationToken);
                if (guestCount >= maxGuests)
                    return Results.Conflict(new { error = "board_guest_limit_reached", maxGuests, guestCount });
                db.BoardMemberships.Add(new BoardMembership
                {
                    BoardId = boardId,
                    UserId = guest.Id,
                    Role = BoardRole.Guest,
                    CanEdit = request.CanEdit
                });
            }
            else
            {
                membership.CanEdit = request.CanEdit;
            }

            if (changed)
            {
                activity.MarkUpdated(db, boardId);
                await db.SaveChangesAsync(cancellationToken);
                if (membership is null)
                {
                    await NotificationSources.BoardAsync(db, boardId, userId, NotificationType.BoardInvitation,
                        "boardInvited", "board", boardId, $"invitation:{Guid.NewGuid():N}", clock.GetUtcNow(),
                        cancellationToken, onlyRecipient: guest.Id);
                    await db.SaveChangesAsync(cancellationToken);
                }
                await transaction.CommitAsync(cancellationToken);
                await realtime.MembersChangedAsync(boardId,
                    downgradedUserId: downgraded ? guest.Id : null);
            }
            else await transaction.CommitAsync(cancellationToken);
            return Results.NoContent();
        }).RequireRateLimiting("board-membership-write");

        group.MapGet("/{boardId:guid}/guest-limit", async (Guid boardId, HttpContext context,
            WuknaDbContext db, IOptionsMonitor<BoardOptions> options, TimeProvider clock, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (await ChatAccess.LockMembershipAsync(db, boardId, userId, ct) is null) return Results.NotFound();
            var guestCount = await db.BoardMemberships.CountAsync(member =>
                member.BoardId == boardId && member.Role == BoardRole.Guest, ct);
            context.Response.Headers.CacheControl = "no-store";
            await transaction.CommitAsync(ct);
            return Results.Ok(new { maxGuests = options.CurrentValue.MaxGuests, guestCount });
        });

        group.MapPatch("/{boardId:guid}/members/{memberId:guid}", async (
            Guid boardId, Guid memberId, SetMemberPermissionRequest request,
            HttpContext context, WuknaDbContext db, BoardActivity activity,
            BoardRealtimeDispatcher realtime, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var callerRole = await db.BoardMemberships.AsNoTracking()
                .Where(membership => membership.BoardId == boardId && membership.UserId == userId)
                .Select(membership => (BoardRole?)membership.Role)
                .SingleOrDefaultAsync(cancellationToken);
            if (callerRole is null) return Results.NotFound();
            if (callerRole != BoardRole.Owner) return Results.Forbid();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            if (!await BoardMembershipLocks.LockBoardAsync(db, boardId, cancellationToken)) return Results.NotFound();
            if ((await ChatAccess.LockMembershipAsync(db, boardId, userId, cancellationToken))?.Role != BoardRole.Owner)
                return Results.NotFound();

            var membership = await db.BoardMemberships.FindAsync([boardId, memberId], cancellationToken);
            if (membership is null) return Results.NotFound();
            if (membership.Role == BoardRole.Owner)
                return Results.Conflict(new { error = "The board owner's permission cannot be changed." });
            if (membership.CanEdit == request.CanEdit) return Results.NoContent();

            var downgraded = membership.CanEdit && !request.CanEdit;
            membership.CanEdit = request.CanEdit;
            activity.MarkUpdated(db, boardId);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new { error = "Membership changed. Reload and try again." });
            }
            await realtime.MembersChangedAsync(boardId, downgradedUserId: downgraded ? memberId : null);
            return Results.NoContent();
        }).RequireRateLimiting("board-membership-write");

        group.MapDelete("/{boardId:guid}/guests/{guestId:guid}", async (
            Guid boardId,
            Guid guestId,
            HttpContext context,
            WuknaDbContext db,
            BoardActivity activity,
            BoardRealtimeDispatcher realtime,
            ChatRevocation chatRevocation,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var callerRole = await db.BoardMemberships.AsNoTracking()
                .Where(membership => membership.BoardId == boardId && membership.UserId == userId)
                .Select(membership => (BoardRole?)membership.Role)
                .SingleOrDefaultAsync(cancellationToken);
            if (callerRole is null) return Results.NotFound();
            if (callerRole != BoardRole.Owner) return Results.Forbid();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            if (!await BoardMembershipLocks.LockBoardAsync(db, boardId, cancellationToken)) return Results.NotFound();
            if ((await ChatAccess.LockMembershipAsync(db, boardId, userId, cancellationToken))?.Role != BoardRole.Owner)
                return Results.NotFound();
            // Serialize with chat joins/sends before reading the membership instance
            // that the durable revocation must invalidate.
            var memberships = await db.BoardMemberships.FromSqlInterpolated($"""
                SELECT * FROM board_memberships
                WHERE board_id = {boardId} AND user_id = {guestId} FOR UPDATE
                """).ToListAsync(cancellationToken);
            var membership = memberships.SingleOrDefault();
            if (membership is null) return Results.NotFound();
            if (membership.Role == BoardRole.Owner)
                return Results.Conflict(new { error = "The board owner cannot be removed." });

            var instance = await db.BoardMemberChatStates.AsNoTracking()
                .Where(state => state.BoardId == boardId && state.UserId == guestId)
                .Select(state => (Guid?)state.MembershipInstanceId).SingleOrDefaultAsync(cancellationToken);
            var chatRevoked = instance is null ? null : chatRevocation.Record(boardId, guestId, instance);
            db.BoardMemberships.Remove(membership);
            activity.MarkUpdated(db, boardId);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Conflict(new { error = "Membership changed. Reload and try again." });
            }
            if (chatRevoked is not null) await chatRevocation.NotifyAsync(chatRevoked);
            await realtime.MembersChangedAsync(boardId, guestId);
            return Results.NoContent();
        }).RequireRateLimiting("board-membership-write");

        return endpoints;
    }

    private static bool TryGetUserId(HttpContext context, out Guid userId) =>
        Guid.TryParse(context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out userId);

    private static Task<bool> IsOwnerAsync(
        WuknaDbContext db,
        Guid boardId,
        Guid userId,
        CancellationToken cancellationToken) =>
        db.BoardMemberships.AnyAsync(
            membership => membership.BoardId == boardId &&
                          membership.UserId == userId &&
                          membership.Role == BoardRole.Owner,
            cancellationToken);
}
