namespace Wukna.Features.Board;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class BoardConfiguration : IEntityTypeConfiguration<Board>
{
    public void Configure(EntityTypeBuilder<Board> entity)
    {
        entity.HasKey(board => board.Id);
        entity.HasIndex(board => board.Title);
        entity.Property(board => board.Title)
              .IsRequired()
              .HasMaxLength(200);
        entity.Property(board => board.CreatedAt).HasDefaultValueSql("now()");
        entity.Property(board => board.UpdatedAt).HasDefaultValueSql("now()");
        entity.Property(board => board.CardColor).HasMaxLength(16);
        entity.Property(board => board.CardColorVersion).HasDefaultValue(0);
        entity.ToTable(table => table.HasCheckConstraint("ck_boards_card_color",
            "card_color IS NULL OR card_color IN ('sage', 'blue', 'lavender', 'clay', 'gold', 'rose')"));
        entity.ToTable(table => table.HasCheckConstraint("ck_boards_card_color_version", "card_color_version >= 0"));
    }
}

public class BoardMembershipConfiguration : IEntityTypeConfiguration<BoardMembership>
{
    public void Configure(EntityTypeBuilder<BoardMembership> entity)
    {
        entity.HasKey(membership => new { membership.BoardId, membership.UserId });
        entity.HasIndex(membership => new { membership.UserId, membership.BoardId });
        // Raw SQL filters are not rewritten by the naming-convention plugin.
        entity.HasIndex(membership => membership.BoardId)
              .IsUnique()
              .HasFilter("\"role\" = 1");

        entity.ToTable(table => table.HasCheckConstraint(
            "ck_board_membership_role",
            "\"role\" IN (0, 1)"));
        entity.ToTable(table => table.HasCheckConstraint(
            "ck_board_membership_owner_can_edit",
            "\"role\" <> 1 OR \"can_edit\""));

        entity.HasOne(membership => membership.Board)
              .WithMany(board => board.Memberships)
              .HasForeignKey(membership => membership.BoardId)
              .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(membership => membership.User)
              .WithMany(user => user.BoardMemberships)
              .HasForeignKey(membership => membership.UserId)
              .OnDelete(DeleteBehavior.Restrict);
    }
}
