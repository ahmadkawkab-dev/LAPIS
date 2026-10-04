namespace Wukna.Features.Profile;

using System.IdentityModel.Tokens.Jwt;
using Wukna.Features.Users;
using Wukna.Features.Realtime;
using Wukna.Shared.Data.AppDbContext;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

public static class ProfileEndpoints
{
    private const long MaxAvatarBytes = 5 * 1024 * 1024;

    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/profile").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapGet("/onboarding", async (HttpContext context, WuknaDbContext db, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var id)) return Results.Unauthorized();
            var state = await db.Users.AsNoTracking().Where(user => user.Id == id)
                .Select(user => new OnboardingDto(user.OnboardingStatus, user.OnboardingVersion))
                .SingleOrDefaultAsync(ct);
            return state is null ? Results.Unauthorized() : Results.Ok(state);
        });
        group.MapPut("/onboarding", async (OnboardingDto request, HttpContext context,
            WuknaDbContext db, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var id)) return Results.Unauthorized();
            if (request.Version != 1 || request.Status is not ("Completed" or "Skipped"))
                return Error(400, "invalid_onboarding_state", "Choose a valid outcome for the current tour.");
            // Only these two preferences change. Older clients cannot erase a later tour version.
            var updated = await db.Users.Where(user => user.Id == id && user.OnboardingVersion <= request.Version)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(user => user.OnboardingStatus, request.Status)
                    .SetProperty(user => user.OnboardingVersion, request.Version), ct);
            if (updated == 0)
                return await db.Users.AnyAsync(user => user.Id == id, ct)
                    ? Error(409, "onboarding_version_changed", "A newer tour has already been saved.")
                    : Results.Unauthorized();
            return Results.Ok(request);
        });
        group.MapGet("", async (HttpContext context, WuknaDbContext db, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var id)) return Results.Unauthorized();
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == id, ct);
            return user is null ? Results.Unauthorized() : Results.Ok(ToDto(user));
        });

        group.MapPatch("", async (
            UpdateProfileRequest request, HttpContext context, WuknaDbContext db,
            BoardRealtimeDispatcher realtime, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var id)) return Results.Unauthorized();
            var user = await db.Users.SingleOrDefaultAsync(candidate => candidate.Id == id, ct);
            if (user is null) return Results.Unauthorized();

            var username = request.Username?.Trim();
            if (username is not null && !UsernamePolicy.IsValid(username))
                return Error(400, "invalid_username", "Use 3–30 letters, numbers, periods, underscores, or hyphens.");
            var displayName = request.DisplayName?.Trim();
            if (displayName?.Length > 80)
                return Error(400, "display_name_too_long", "Display name must be 80 characters or fewer.");
            if (displayName?.Length == 0) displayName = null;

            if (username is not null && !string.Equals(username, user.Username, StringComparison.Ordinal))
            {
                var normalized = UsernamePolicy.Normalize(username);
                if (await db.Users.AsNoTracking().AnyAsync(candidate =>
                    candidate.Id != id && candidate.NormalizedUsername == normalized, ct))
                    return Error(409, "username_taken", "That username is already in use.");
                user.Username = username;
                user.NormalizedUsername = normalized;
            }
            user.DisplayName = displayName;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException exception) when (IsUsernameCollision(exception))
            {
                return Error(409, "username_taken", "That username is already in use.");
            }
            var changed = ToDto(user);
            await realtime.ProfileChangedAsync(id);
            return Results.Ok(changed);
        });

        group.MapPut("/avatar", async (
            HttpContext context,
            IProfileImageStore store,
            WuknaDbContext db,
            BoardRealtimeDispatcher realtime,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var id)) return Results.Unauthorized();
            var form = await context.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
                return Error(400, "avatar_invalid_image", "Choose an image file.");
            if (file.Length > MaxAvatarBytes)
                return Error(413, "avatar_too_large", "Profile images must be 5 MB or smaller.");
            if (file.ContentType is not ("image/jpeg" or "image/png" or "image/webp"))
                return Error(415, "avatar_invalid_type", "Use a JPEG, PNG, or WebP image.");

            using var input = new MemoryStream((int)file.Length);
            await file.CopyToAsync(input, ct);
            input.Position = 0;
            ImageInfo? imageInfo;
            try { imageInfo = await Image.IdentifyAsync(input, ct); }
            catch (Exception exception) when (exception is UnknownImageFormatException or InvalidImageContentException)
            {
                return Error(400, "avatar_invalid_image", "The selected file is not a valid image.");
            }
            if (imageInfo is null)
                return Error(400, "avatar_invalid_image", "The selected file is not a valid image.");
            if (imageInfo.Width < 1 || imageInfo.Height < 1 || imageInfo.Width > 4096 || imageInfo.Height > 4096 ||
                (long)imageInfo.Width * imageInfo.Height > 16_000_000)
                return Error(400, "avatar_invalid_image", "Image dimensions must be 4096 × 4096 or smaller.");
            input.Position = 0;
            Image image;
            try
            {
                image = await Image.LoadAsync(input, ct);
            }
            catch (Exception exception) when (exception is UnknownImageFormatException or InvalidImageContentException)
            {
                return Error(400, "avatar_invalid_image", "The selected file is not a valid image.");
            }

            using (image)
            {
                image.Mutate(processing => processing.AutoOrient());
                var format = image.Metadata.DecodedImageFormat?.Name;
                var accepted = file.ContentType switch
                {
                    "image/jpeg" => format == "JPEG",
                    "image/png" => format == "PNG",
                    "image/webp" => format == "Webp",
                    _ => false
                };
                if (!accepted) return Error(415, "avatar_invalid_type", "The image content does not match its media type.");

                image.Metadata.ExifProfile = null;
                image.Metadata.IccProfile = null;
                image.Metadata.XmpProfile = null;
                image.Metadata.IptcProfile = null;
                var key = $"{Guid.NewGuid():N}.webp";
                var version = Guid.NewGuid().ToString("N");
                await using var encoded = new MemoryStream();
                await image.SaveAsync(encoded, new WebpEncoder { Quality = 82 }, ct);
                encoded.Position = 0;
                await store.SaveAsync(key, encoded, ct);

                var user = await db.Users.SingleOrDefaultAsync(candidate => candidate.Id == id, ct);
                if (user is null)
                {
                    await store.DeleteAsync(key, ct);
                    return Results.Unauthorized();
                }
                var oldKey = user.ProfileImageKey;
                user.ProfileImageKey = key;
                user.ProfileImageVersion = version;
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch
                {
                    await store.DeleteAsync(key, CancellationToken.None);
                    throw;
                }

                if (oldKey is not null)
                {
                    try { await store.DeleteAsync(oldKey, ct); }
                    catch (Exception exception)
                    {
                        loggerFactory.CreateLogger("ProfileImages").LogWarning(exception, "Could not remove replaced profile image.");
                    }
                }
                await realtime.ProfileChangedAsync(id);
                return Results.Ok(new AvatarDto(ProfileImageUrls.For(key, version), version));
            }
        }).DisableAntiforgery();

        group.MapDelete("/avatar", async (
            HttpContext context, WuknaDbContext db, IProfileImageStore store, ILoggerFactory loggerFactory,
            BoardRealtimeDispatcher realtime, CancellationToken ct) =>
        {
            if (!TryGetUserId(context, out var id)) return Results.Unauthorized();
            var user = await db.Users.SingleOrDefaultAsync(candidate => candidate.Id == id, ct);
            if (user is null) return Results.Unauthorized();
            var oldKey = user.ProfileImageKey;
            user.ProfileImageKey = null;
            user.ProfileImageVersion = null;
            await db.SaveChangesAsync(ct);
            if (oldKey is not null)
            {
                try { await store.DeleteAsync(oldKey, ct); }
                catch (Exception exception) { loggerFactory.CreateLogger("ProfileImages").LogWarning(exception, "Could not remove profile image."); }
            }
            await realtime.ProfileChangedAsync(id);
            return Results.Ok(new AvatarDto(null, null));
        });

        return endpoints;
    }

    private static bool TryGetUserId(HttpContext context, out Guid id) =>
        Guid.TryParse(context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out id);

    private static ProfileDto ToDto(User user) => new(
        user.Id, user.Username, user.DisplayName, user.Email ?? string.Empty,
        ProfileImageUrls.For(user.ProfileImageKey, user.ProfileImageVersion), user.ProfileImageVersion);

    private static IResult Error(int status, string code, string message) => Results.Problem(
        statusCode: status,
        title: message,
        extensions: new Dictionary<string, object?> { ["code"] = code, ["message"] = message });

    private static bool IsUsernameCollision(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ix_asp_net_users_normalized_username" };
}
