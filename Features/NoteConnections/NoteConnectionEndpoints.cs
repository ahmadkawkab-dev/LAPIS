namespace Wukna.Features.NoteConnection;

using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using Wukna.Features.Board;
using Wukna.Features.Notes;
using Wukna.Features.Realtime;
using Wukna.Shared.Data.AppDbContext;
using Microsoft.EntityFrameworkCore;

public sealed record CreateNoteConnectionRequest(
    Guid SourceNoteId, Guid TargetNoteId, ConnectionType Type,
    string SourceHandle = "right", string TargetHandle = "left");

public sealed record ReconnectNoteConnectionRequest(
    Guid SourceNoteId, Guid TargetNoteId, string SourceHandle, string TargetHandle);

public sealed record NoteConnectionDto(
    Guid Id, Guid BoardId, Guid SourceNoteId, Guid TargetNoteId,
    ConnectionType Type, DateTimeOffset CreatedAt,
    string SourceHandle = "right", string TargetHandle = "left", uint Version = 0)
{
    public static NoteConnectionDto From(NoteConnection connection) => new(
        connection.Id, connection.BoardId, connection.SourceNoteId,
        connection.TargetNoteId, connection.Type, connection.CreatedAt,
        connection.SourceHandle, connection.TargetHandle, connection.Version);
}

public static class NoteConnectionEndpoints
{
    public static IEndpointRouteBuilder MapNoteConnectionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/boards/{boardId:guid}/connections")
            .RequireAuthorization();

        group.MapGet("/", async (Guid boardId, HttpContext context, WuknaDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (await GetAccessAsync(db, boardId, userId, cancellationToken) is null)
                return Results.NotFound();
            var connections = await db.NoteConnections.AsNoTracking()
                .Where(c => c.BoardId == boardId)
                .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
                .ToListAsync(cancellationToken);
            return Results.Ok(connections.Select(NoteConnectionDto.From));
        });

        group.MapPost("/", async (Guid boardId, CreateNoteConnectionRequest request,
            HttpContext context, WuknaDbContext db, BoardActivity activity,
            BoardRealtimeDispatcher realtime, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var canEdit = await GetAccessAsync(db, boardId, userId, cancellationToken);
            if (canEdit is null) return Results.NotFound();
            if (!canEdit.Value) return Results.Forbid();
            if (request.SourceNoteId == request.TargetNoteId ||
                request.Type is not (ConnectionType.Related or ConnectionType.Prerequisite))
                return Results.BadRequest(new { error = "invalid_connection" });
            if (!ValidHandle(request.SourceHandle) || !ValidHandle(request.TargetHandle))
                return Results.BadRequest(new { error = "invalid_connection_handle" });

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await LockConnectionsAsync(db, boardId, cancellationToken);
            if (!await ValidNotesAsync(db, boardId, request.SourceNoteId, request.TargetNoteId, cancellationToken))
                return Results.BadRequest(new { error = "invalid_connection_notes" });
            if (await ExistsAsync(db, boardId, request.SourceNoteId, request.TargetNoteId, null, cancellationToken))
                return Results.Conflict(new { error = "connection_exists" });
            var connection = new NoteConnection
            {
                BoardId = boardId, SourceNoteId = request.SourceNoteId, TargetNoteId = request.TargetNoteId,
                SourceHandle = request.SourceHandle, TargetHandle = request.TargetHandle,
                Type = request.Type, Color = "#888888"
            };
            db.NoteConnections.Add(connection);
            activity.MarkUpdated(db, boardId);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            context.Response.Headers.ETag = $"\"{connection.Version}\"";
            var response = NoteConnectionDto.From(connection);
            await realtime.ConnectionCreatedAsync(response);
            return Results.Created($"/api/boards/{boardId}/connections/{connection.Id}", response);
        });

        group.MapPatch("/{connectionId:guid}", async (Guid boardId, Guid connectionId,
            ReconnectNoteConnectionRequest request, HttpContext context, WuknaDbContext db,
            BoardActivity activity, BoardRealtimeDispatcher realtime, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var canEdit = await GetAccessAsync(db, boardId, userId, cancellationToken);
            if (canEdit is null) return Results.NotFound();
            if (!canEdit.Value) return Results.Forbid();
            var versionError = ReadVersion(context, out var version);
            if (versionError is not null) return versionError;
            if (request.SourceNoteId == request.TargetNoteId)
                return Results.BadRequest(new { error = "invalid_connection" });
            if (!ValidHandle(request.SourceHandle) || !ValidHandle(request.TargetHandle))
                return Results.BadRequest(new { error = "invalid_connection_handle" });

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await LockConnectionsAsync(db, boardId, cancellationToken);
            var connection = await db.NoteConnections.SingleOrDefaultAsync(c => c.BoardId == boardId && c.Id == connectionId, cancellationToken);
            if (connection is null) return Results.NotFound();
            if (connection.Version != version) return VersionConflict();
            if (!await ValidNotesAsync(db, boardId, request.SourceNoteId, request.TargetNoteId, cancellationToken))
                return Results.BadRequest(new { error = "invalid_connection_notes" });
            if (await ExistsAsync(db, boardId, request.SourceNoteId, request.TargetNoteId, connectionId, cancellationToken))
                return Results.Conflict(new { error = "connection_exists" });
            connection.SourceNoteId = request.SourceNoteId;
            connection.TargetNoteId = request.TargetNoteId;
            connection.SourceHandle = request.SourceHandle;
            connection.TargetHandle = request.TargetHandle;
            activity.MarkUpdated(db, boardId);
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateConcurrencyException) { return VersionConflict(); }
            await transaction.CommitAsync(cancellationToken);
            context.Response.Headers.ETag = $"\"{connection.Version}\"";
            var response = NoteConnectionDto.From(connection);
            await realtime.ConnectionUpdatedAsync(response);
            return Results.Ok(response);
        });

        group.MapDelete("/{connectionId:guid}", async (Guid boardId, Guid connectionId,
            HttpContext context, WuknaDbContext db, BoardActivity activity,
            BoardRealtimeDispatcher realtime, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var canEdit = await GetAccessAsync(db, boardId, userId, cancellationToken);
            if (canEdit is null) return Results.NotFound();
            if (!canEdit.Value) return Results.Forbid();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await LockConnectionsAsync(db, boardId, cancellationToken);
            var connection = await db.NoteConnections.SingleOrDefaultAsync(c => c.BoardId == boardId && c.Id == connectionId, cancellationToken);
            if (connection is null) return Results.NotFound();
            db.NoteConnections.Remove(connection);
            activity.MarkUpdated(db, boardId);
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateConcurrencyException) { return VersionConflict(); }
            await transaction.CommitAsync(cancellationToken);
            await realtime.ConnectionDeletedAsync(new ConnectionDeletedEvent(boardId, connectionId, connection.Version));
            return Results.NoContent();
        });
        return endpoints;
    }

    private static bool ValidHandle(string? handle) => handle is "top" or "right" or "bottom" or "left";
    private static async Task<bool> ValidNotesAsync(WuknaDbContext db, Guid boardId, Guid sourceId, Guid targetId, CancellationToken cancellationToken) =>
        await db.Notes.AsNoTracking().CountAsync(n => n.BoardId == boardId && n.Kind != NoteKind.ChecklistItem &&
            (n.Id == sourceId || n.Id == targetId), cancellationToken) == 2;
    private static Task<bool> ExistsAsync(WuknaDbContext db, Guid boardId, Guid sourceId, Guid targetId, Guid? exceptId, CancellationToken cancellationToken) =>
        db.NoteConnections.AnyAsync(c => c.BoardId == boardId && (exceptId == null || c.Id != exceptId) &&
            ((c.SourceNoteId == sourceId && c.TargetNoteId == targetId) ||
             (c.SourceNoteId == targetId && c.TargetNoteId == sourceId)), cancellationToken);
    // Serialize only connection writes on a board, including reversed duplicates, without changing note/camera state.
    private static Task<int> LockConnectionsAsync(WuknaDbContext db, Guid boardId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({boardId.ToString()}, 0))", cancellationToken);
    private static IResult VersionConflict() => Results.Conflict(new { error = "connection_version_conflict" });
    private static IResult? ReadVersion(HttpContext context, out uint version)
    {
        version = 0;
        if (!context.Request.Headers.TryGetValue("If-Match", out var header))
            return Results.Json(new { error = "connection_version_required" }, statusCode: StatusCodes.Status428PreconditionRequired);
        var value = header.ToString();
        return value.Length >= 3 && value[0] == '"' && value[^1] == '"' &&
            uint.TryParse(value[1..^1], NumberStyles.None, CultureInfo.InvariantCulture, out version)
            ? null : Results.BadRequest(new { error = "invalid_connection_version" });
    }
    private static bool TryGetUserId(HttpContext context, out Guid userId) =>
        Guid.TryParse(context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out userId);
    private static async Task<bool?> GetAccessAsync(WuknaDbContext db, Guid boardId, Guid userId, CancellationToken cancellationToken)
    {
        var membership = await db.BoardMemberships.AsNoTracking()
            .Where(m => m.BoardId == boardId && m.UserId == userId)
            .Select(m => new { m.Role, m.CanEdit }).SingleOrDefaultAsync(cancellationToken);
        return membership is null ? null : membership.Role == BoardRole.Owner || membership.CanEdit;
    }
}
