namespace Wukna.Features.Calendar;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class CalendarEventConfiguration : IEntityTypeConfiguration<CalendarEvent>
{
    public void Configure(EntityTypeBuilder<CalendarEvent> entity)
    {
        entity.ToTable("calendar_events", table => table.HasCheckConstraint(
            "ck_calendar_event_schedule",
            "(is_all_day AND all_day_start_date IS NOT NULL AND all_day_end_date_exclusive IS NOT NULL " +
            "AND all_day_end_date_exclusive > all_day_start_date AND local_start IS NULL AND local_end IS NULL " +
            "AND time_zone_id IS NULL AND start_at_utc IS NULL AND end_at_utc IS NULL) OR " +
            "(NOT is_all_day AND all_day_start_date IS NULL AND all_day_end_date_exclusive IS NULL " +
            "AND local_start IS NOT NULL AND local_end IS NOT NULL AND local_end > local_start " +
            "AND time_zone_id IS NOT NULL AND start_at_utc IS NOT NULL AND end_at_utc IS NOT NULL " +
            "AND end_at_utc > start_at_utc)"));
        entity.HasKey(item => item.Id);
        entity.Property(item => item.Title).HasMaxLength(200).IsRequired();
        entity.Property(item => item.Description).HasMaxLength(4000);
        entity.Property(item => item.Location).HasMaxLength(200);
        entity.Property(item => item.TimeZoneId).HasMaxLength(100);
        entity.Property(item => item.LocalStart).HasColumnType("timestamp without time zone");
        entity.Property(item => item.LocalEnd).HasColumnType("timestamp without time zone");
        entity.Property(item => item.CreatedAt).HasDefaultValueSql("now()");
        entity.Property(item => item.UpdatedAt).HasDefaultValueSql("now()");
        entity.HasOne(item => item.User).WithMany()
            .HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(item => new { item.UserId, item.AllDayStartDate, item.AllDayEndDateExclusive });
        entity.HasIndex(item => new { item.UserId, item.StartAtUtc, item.EndAtUtc });
    }
}
