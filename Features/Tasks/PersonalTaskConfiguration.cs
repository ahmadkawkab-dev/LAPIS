namespace Wukna.Features.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class PersonalTaskConfiguration : IEntityTypeConfiguration<PersonalTask>
{
    public void Configure(EntityTypeBuilder<PersonalTask> entity)
    {
        entity.ToTable("personal_tasks", table => table.HasCheckConstraint(
            "ck_personal_task_schedule",
            "(planned_time IS NULL AND time_zone_id IS NULL AND planned_at_utc IS NULL) OR " +
            "(planned_date IS NOT NULL AND planned_time IS NOT NULL AND time_zone_id IS NOT NULL AND planned_at_utc IS NOT NULL)"));
        entity.HasKey(task => task.Id);
        entity.Property(task => task.Title).HasMaxLength(200).IsRequired();
        entity.Property(task => task.Description).HasMaxLength(4000);
        entity.Property(task => task.TimeZoneId).HasMaxLength(100);
        entity.Property(task => task.CreatedAt).HasDefaultValueSql("now()");
        entity.Property(task => task.UpdatedAt).HasDefaultValueSql("now()");
        entity.HasOne(task => task.User).WithMany()
            .HasForeignKey(task => task.UserId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(task => task.List).WithMany()
            .HasForeignKey(task => task.ListId).OnDelete(DeleteBehavior.Cascade);
        // Inbox, Today, and Upcoming all filter by user, completion, and planned date.
        entity.HasIndex(task => new { task.UserId, task.CompletedAt, task.PlannedDate });
        entity.HasIndex(task => new { task.UserId, task.ListId });
    }
}
