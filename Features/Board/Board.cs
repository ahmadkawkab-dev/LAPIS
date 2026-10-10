namespace Wukna.Features.Board;

using Features.Notes;

public class Board
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string? CardColor { get; set; }
    public int CardColorVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Collection navigations remain mutable so entities can be added, but the collection
    // instance can't be replaced accidentally.
    public ICollection<BoardMembership> Memberships { get; } = new List<BoardMembership>();
    public ICollection<Note> Notes { get; } = new List<Note>();
}
