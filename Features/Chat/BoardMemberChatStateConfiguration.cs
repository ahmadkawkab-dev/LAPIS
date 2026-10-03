namespace Wukna.Features.Chat;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class BoardMemberChatStateConfiguration : IEntityTypeConfiguration<BoardMemberChatState>
{
    public void Configure(EntityTypeBuilder<BoardMemberChatState> entity)
    {
        entity.ToTable("board_member_chat_states", table =>
        {
            table.HasCheckConstraint("ck_board_member_chat_state_counters",
                "moderation_revision >= 0 AND cooldown_settings_revision >= 0 AND last_read_sequence >= 0");
            table.HasCheckConstraint("ck_board_member_chat_state_mute", "is_muted OR muted_until IS NULL");
            table.HasCheckConstraint("ck_board_member_chat_state_instance",
                "membership_instance_id <> '00000000-0000-0000-0000-000000000000'::uuid");
        });
        entity.HasKey(state => new { state.BoardId, state.UserId });
        entity.HasOne(state => state.Membership).WithOne()
            .HasForeignKey<BoardMemberChatState>(state => new { state.BoardId, state.UserId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
