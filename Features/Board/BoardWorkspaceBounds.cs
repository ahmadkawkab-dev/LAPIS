namespace Wukna.Features.Board;

using Wukna.Features.Notes;

/// <summary>The finite world shared with frontend boardBounds.ts, independent of each user's camera.</summary>
public static class BoardWorkspaceBounds
{
    public const double Left = -6000;
    public const double Top = -4000;
    public const double Right = 6000;
    public const double Bottom = 4000;

    public static void Constrain(Note note)
    {
        if (note.Kind == NoteKind.ChecklistItem) return;
        note.Width = Math.Min(note.Width, Right - Left);
        note.Height = Math.Min(note.Height, Bottom - Top);
        note.PositionX = Math.Clamp(note.PositionX ?? 0, Left, Right - note.Width);
        note.PositionY = Math.Clamp(note.PositionY ?? 0, Top, Bottom - note.Height);
    }
}
