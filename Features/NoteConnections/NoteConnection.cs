namespace Wukna.Features.NoteConnection;

using Features.Notes;

public enum ConnectionType
{
    Related = 0,
    Prerequisite = 1
}

public class NoteConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BoardId { get; set; }
    public uint Version { get; set; }
    public ConnectionType Type { get; set; } = ConnectionType.Related;

    public Guid SourceNoteId { get; set; }
    public Note SourceNote { get; set; } = null!;
    public string SourceHandle { get; set;} = "right";

    public Guid TargetNoteId { get; set; }
    public Note TargetNote { get; set; } = null!;
    public string TargetHandle { get; set; } = "left";

    public string? Label { get; set; }
    public string LineType { get; set; } = "bezier";
    public string Color { get; set; } = "#88888";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

}
