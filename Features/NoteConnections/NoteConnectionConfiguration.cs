namespace Wukna.Features.NoteConnection;

using Wukna.Features.Notes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class NoteConnectionConfiguration : IEntityTypeConfiguration<NoteConnection>
{
    public void Configure(EntityTypeBuilder<NoteConnection> entity)
    {
        entity.HasKey(connection => connection.Id);
        entity.Property(connection => connection.Version).IsRowVersion();
        entity.HasIndex(connection => new { connection.BoardId, connection.SourceNoteId });
        entity.HasIndex(connection => new { connection.BoardId, connection.TargetNoteId });

        entity.ToTable(table => table.HasCheckConstraint(
            "ck_note_connection_type",
            "\"type\" IN (0, 1)"));

        entity.HasOne(connection => connection.SourceNote)
              .WithMany(note => note.OutgoingConnections)
              .HasForeignKey(connection => new { connection.BoardId, connection.SourceNoteId })
              .HasPrincipalKey(note => new { note.BoardId, note.Id })
              .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(connection => connection.TargetNote)
              .WithMany(note => note.IncomingConnections)
              .HasForeignKey(connection => new { connection.BoardId, connection.TargetNoteId })
              .HasPrincipalKey(note => new { note.BoardId, note.Id })
              .OnDelete(DeleteBehavior.Cascade);
    }
}
