namespace Wukna.Features.Chat;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class BoardChatSettingsConfiguration : IEntityTypeConfiguration<BoardChatSettings>
{
    public void Configure(EntityTypeBuilder<BoardChatSettings> entity)
    {
        entity.ToTable("board_chat_settings", table =>
        {
            table.HasCheckConstraint("ck_board_chat_settings_cooldown", "slow_mode_seconds BETWEEN 0 AND 21600");
            table.HasCheckConstraint("ck_board_chat_settings_counters", "settings_revision > 0 AND last_message_sequence >= 0");
        });
        entity.HasKey(settings => settings.BoardId);
        entity.Property(settings => settings.BoardId).ValueGeneratedNever();
        entity.HasOne(settings => settings.Board).WithOne()
            .HasForeignKey<BoardChatSettings>(settings => settings.BoardId).OnDelete(DeleteBehavior.Cascade);
    }
}
