namespace Wukna.Features.Chat;

using System.Globalization;
using Wukna.Features.Board;

internal static class ChatSendPolicy
{
    internal static DateTimeOffset? NextSend(BoardChatSettings settings, BoardMemberChatState? state, BoardRole role) =>
        role == BoardRole.Owner || settings.SlowModeSeconds == 0 || state is null || state.CooldownSettingsRevision != settings.SettingsRevision
            ? null : state.NextSendAllowedAt;

    internal static IResult? Rejection(HttpContext context, BoardChatSettings settings, BoardMemberChatState? state,
        BoardRole role, DateTimeOffset now)
    {
        if (state is not null && ChatModeration.IsMuted(state, now))
            return Results.Json(new { error = "chat_muted", state.MutedUntil, serverTime = now,
                state.ModerationRevision, state.MembershipInstanceId }, statusCode: 403);
        if (NextSend(settings, state, role) is not { } next || next <= now) return null;
        context.Response.Headers.RetryAfter = Math.Ceiling((next - now).TotalSeconds).ToString(CultureInfo.InvariantCulture);
        return Results.Json(new { error = "chat_cooldown", serverTime = now, nextSendAllowedAt = next,
            settingsRevision = settings.SettingsRevision }, statusCode: 429);
    }

    internal static void Consume(BoardChatSettings settings, BoardMemberChatState state, BoardRole role, DateTimeOffset now)
    {
        state.CooldownSettingsRevision = settings.SettingsRevision;
        state.NextSendAllowedAt = role == BoardRole.Owner || settings.SlowModeSeconds == 0 ? null : now.AddSeconds(settings.SlowModeSeconds);
    }
}
