namespace Wukna.Features.Chat;

using Wukna.Features.Board;

public sealed class BoardChatSettings
{
    public Guid BoardId { get; set; }
    public Board Board { get; set; } = null!;
    // Zero is normal mode. Commands lock this row before allocating a sequence.
    public int SlowModeSeconds { get; set; }
    public long SettingsRevision { get; set; } = 1;
    public long LastMessageSequence { get; set; }
}
