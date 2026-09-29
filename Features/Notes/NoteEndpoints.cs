namespace Wukna.Features.Notes;

using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Text.RegularExpressions;
using Wukna.Features.Board;
using Wukna.Features.Realtime;
using Wukna.Shared.Data.AppDbContext;
using Microsoft.EntityFrameworkCore;

public sealed record CreateNoteRequest(
    NoteKind? Kind,
    string? Title,
    string? Content,
    Guid? ParentNoteId,
    double? PositionX,
    double? PositionY,
    double? Width,
    double? Height,
    int? ZIndex,
    string? Color,
    bool? IsCompleted);

public sealed record PatchNoteRequest(
    string? Title,
    string? Content,
    double? PositionX,
    double? PositionY,
    double? Width,
    double? Height,
    int? ZIndex,
    string? Color,
    bool? IsCompleted);

public sealed record NoteDto(
    Guid Id,
    Guid BoardId,
    NoteKind Kind,
    Guid? ParentNoteId,
    string Title,
    string Content,
    double? PositionX,
    double? PositionY,
    double Width,
    double Height,
    int ZIndex,
    string Color,
    bool IsCompleted,
    DateTimeOffset CreatedAt,
    uint Version)
{
    public static NoteDto From(Note note) => new(
        note.Id, note.BoardId, note.Kind, note.ParentNoteId, note.Title, note.Content,
        note.PositionX, note.PositionY, note.Width, note.Height, note.ZIndex,
        note.Color, note.IsCompleted, note.CreatedAt, note.Version);
}

public static class NoteEndpoints
{
    private sealed record BoardAccess(bool CanEdit);

    public static IEndpointRouteBuilder MapNoteEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/boards/{boardId:guid}/notes").RequireAuthorization();

        group.MapGet("/", async (Guid boardId, HttpContext context, WuknaDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (await GetAccessAsync(db, boardId, userId, cancellationToken) is null)
                return Results.NotFound();

            var notes = await db.Notes.AsNoTracking()
                .Where(note => note.BoardId == boardId)
                .OrderBy(note => note.CreatedAt).ThenBy(note => note.Id)
                .ToListAsync(cancellationToken);
            return Results.Ok(notes.Select(NoteDto.From));
        });

        group.MapGet("/{noteId:guid}", async (Guid boardId, Guid noteId,
            HttpContext context, WuknaDbContext db, CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            if (await GetAccessAsync(db, boardId, userId, cancellationToken) is null)
                return Results.NotFound();

            var note = await db.Notes.AsNoTracking().SingleOrDefaultAsync(
                candidate => candidate.BoardId == boardId && candidate.Id == noteId,
                cancellationToken);
            if (note is null) return Results.NotFound();
            SetEtag(context, note.Version);
            return Results.Ok(NoteDto.From(note));
        });

        group.MapPost("/", async (Guid boardId, CreateNoteRequest request,
            HttpContext context, WuknaDbContext db, BoardActivity activity,
            BoardRealtimeDispatcher realtime,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var access = await GetAccessAsync(db, boardId, userId, cancellationToken);
            if (access is null) return Results.NotFound();
            if (!access.CanEdit) return Results.Forbid();

            if (request.Kind is not (NoteKind.Standalone or NoteKind.List or NoteKind.ChecklistItem))
                return BadRequest("invalid_note_kind");
            if (!ValidTitle(request.Title)) return BadRequest("invalid_note_title");
            if (request.Width is not null && !PositiveFinite(request.Width.Value) ||
                request.Height is not null && !PositiveFinite(request.Height.Value))
                return BadRequest("invalid_note_dimensions");
            if (request.Color is not null && !ValidColor(request.Color))
                return BadRequest("invalid_note_color");

            if (request.Kind == NoteKind.ChecklistItem)
            {
                if (request.ParentNoteId is null || request.PositionX is not null ||
                    request.PositionY is not null)
                    return BadRequest("invalid_note_parent_or_position");

                var parentIsList = await db.Notes.AsNoTracking().AnyAsync(
                    parent => parent.BoardId == boardId &&
                              parent.Id == request.ParentNoteId && parent.Kind == NoteKind.List,
                    cancellationToken);
                if (!parentIsList) return BadRequest("invalid_note_parent");
            }
            else if (request.ParentNoteId is not null ||
                     request.PositionX is not null && !double.IsFinite(request.PositionX.Value) ||
                     request.PositionY is not null && !double.IsFinite(request.PositionY.Value))
            {
                return BadRequest("invalid_note_parent_or_position");
            }

            var note = new Note
            {
                BoardId = boardId,
                Kind = request.Kind.Value,
                ParentNoteId = request.ParentNoteId,
                Title = request.Title!.Trim(),
                Content = request.Content ?? string.Empty,
                PositionX = request.Kind == NoteKind.ChecklistItem ? null : request.PositionX ?? 0,
                PositionY = request.Kind == NoteKind.ChecklistItem ? null : request.PositionY ?? 0,
                Width = request.Width ?? 240,
                Height = request.Height ?? 160,
                ZIndex = request.ZIndex ?? 1,
                Color = request.Color ?? "#FFFFFF",
                IsCompleted = request.IsCompleted ?? false
            };
            BoardWorkspaceBounds.Constrain(note);
            db.Notes.Add(note);
            activity.MarkUpdated(db, boardId);
            await db.SaveChangesAsync(cancellationToken);
            SetEtag(context, note.Version);
            var response = NoteDto.From(note);
            await realtime.NoteCreatedAsync(response);
            return Results.Created($"/api/boards/{boardId}/notes/{note.Id}", response);
        });

        group.MapPatch("/{noteId:guid}", async (Guid boardId, Guid noteId,
            PatchNoteRequest request, HttpContext context, WuknaDbContext db,
            BoardActivity activity,
            BoardRealtimeDispatcher realtime,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var access = await GetAccessAsync(db, boardId, userId, cancellationToken);
            if (access is null) return Results.NotFound();
            if (!access.CanEdit) return Results.Forbid();
            var versionError = ReadVersion(context, out var version);
            if (versionError is not null) return versionError;

            var note = await db.Notes.SingleOrDefaultAsync(
                candidate => candidate.BoardId == boardId && candidate.Id == noteId,
                cancellationToken);
            if (note is null) return Results.NotFound();
            if (note.Version != version) return VersionConflict();
            if (request.Title is not null && !ValidTitle(request.Title))
                return BadRequest("invalid_note_title");
            if (request.Width is not null && !PositiveFinite(request.Width.Value) ||
                request.Height is not null && !PositiveFinite(request.Height.Value))
                return BadRequest("invalid_note_dimensions");
            if (request.Color is not null && !ValidColor(request.Color))
                return BadRequest("invalid_note_color");
            if (note.Kind == NoteKind.ChecklistItem &&
                (request.PositionX is not null || request.PositionY is not null) ||
                request.PositionX is not null && !double.IsFinite(request.PositionX.Value) ||
                request.PositionY is not null && !double.IsFinite(request.PositionY.Value))
                return BadRequest("invalid_note_position");

            if (request.Title is not null) note.Title = request.Title.Trim();
            if (request.Content is not null) note.Content = request.Content;
            if (request.PositionX is not null) note.PositionX = request.PositionX;
            if (request.PositionY is not null) note.PositionY = request.PositionY;
            if (request.Width is not null) note.Width = request.Width.Value;
            if (request.Height is not null) note.Height = request.Height.Value;
            if (request.PositionX is not null || request.PositionY is not null ||
                request.Width is not null || request.Height is not null)
                BoardWorkspaceBounds.Constrain(note);
            if (request.ZIndex is not null) note.ZIndex = request.ZIndex.Value;
            if (request.Color is not null) note.Color = request.Color;
            if (request.IsCompleted is not null) note.IsCompleted = request.IsCompleted.Value;

            db.ChangeTracker.DetectChanges();
            var changed = db.Entry(note).Properties.Any(property => property.IsModified);
            if (changed)
                activity.MarkUpdated(db, boardId);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return VersionConflict();
            }

            SetEtag(context, note.Version);
            var response = NoteDto.From(note);
            if (changed) await realtime.NoteUpdatedAsync(response);
            return Results.Ok(response);
        });

        group.MapDelete("/{noteId:guid}", async (Guid boardId, Guid noteId,
            HttpContext context, WuknaDbContext db, BoardActivity activity,
            BoardRealtimeDispatcher realtime,
            CancellationToken cancellationToken) =>
        {
            if (!TryGetUserId(context, out var userId)) return Results.Unauthorized();
            var access = await GetAccessAsync(db, boardId, userId, cancellationToken);
            if (access is null) return Results.NotFound();
            if (!access.CanEdit) return Results.Forbid();
            var versionError = ReadVersion(context, out var version);
            if (versionError is not null) return versionError;

            var note = await db.Notes.SingleOrDefaultAsync(
                candidate => candidate.BoardId == boardId && candidate.Id == noteId,
                cancellationToken);
            if (note is null) return Results.NotFound();
            if (note.Version != version) return VersionConflict();
            if (await db.Notes.AnyAsync(candidate => candidate.BoardId == boardId &&
                    candidate.ParentNoteId == noteId, cancellationToken))
                return Results.Conflict(new { error = "note_has_checklist_items" });

            db.Notes.Remove(note);
            activity.MarkUpdated(db, boardId);
            var deletedVersion = note.Version;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return VersionConflict();
            }
            await realtime.NoteDeletedAsync(new NoteDeletedEvent(
                boardId, noteId, deletedVersion));
            return Results.NoContent();
        });

        return endpoints;
    }

    private static bool TryGetUserId(HttpContext context, out Guid userId) =>
        Guid.TryParse(context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out userId);

    private static Task<BoardAccess?> GetAccessAsync(WuknaDbContext db, Guid boardId,
        Guid userId, CancellationToken cancellationToken) =>
        db.BoardMemberships.AsNoTracking()
            .Where(membership => membership.BoardId == boardId && membership.UserId == userId)
            .Select(membership => new BoardAccess(
                membership.Role == BoardRole.Owner || membership.CanEdit))
            .SingleOrDefaultAsync(cancellationToken);

    private static bool ValidTitle(string? title) =>
        !string.IsNullOrWhiteSpace(title) && title.Trim().Length <= 200;

    private static bool PositiveFinite(double value) => double.IsFinite(value) && value > 0;

    private static bool ValidColor(string color) =>
        Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$");

    private static IResult BadRequest(string error) => Results.BadRequest(new { error });

    private static IResult VersionConflict() => Results.Conflict(new { error = "note_version_conflict" });

    private static IResult? ReadVersion(HttpContext context, out uint version)
    {
        version = 0;
        if (!context.Request.Headers.TryGetValue("If-Match", out var header))
            return Results.Json(new { error = "note_version_required" },
                statusCode: StatusCodes.Status428PreconditionRequired);

        var value = header.ToString();
        if (value.Length < 3 || value[0] != '"' || value[^1] != '"' ||
            !uint.TryParse(value[1..^1], NumberStyles.None, CultureInfo.InvariantCulture,
                out version))
            return BadRequest("invalid_note_version");

        return null;
    }

    private static void SetEtag(HttpContext context, uint version) =>
        context.Response.Headers.ETag = $"\"{version}\"";
}
