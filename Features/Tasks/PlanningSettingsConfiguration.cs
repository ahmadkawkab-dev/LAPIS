namespace Wukna.Features.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class PlanningSettingsConfiguration : IEntityTypeConfiguration<PlanningSettings>
{
    public void Configure(EntityTypeBuilder<PlanningSettings> entity)
    {
        entity.ToTable("planning_settings");
        entity.HasKey(settings => settings.UserId);
        entity.Property(settings => settings.TimeZoneId).HasMaxLength(100).IsRequired();
        entity.Property(settings => settings.UpdatedAt).HasDefaultValueSql("now()");
        entity.HasOne(settings => settings.User).WithOne()
            .HasForeignKey<PlanningSettings>(settings => settings.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
