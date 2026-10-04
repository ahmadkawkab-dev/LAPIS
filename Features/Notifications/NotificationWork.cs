namespace Wukna.Features.Notifications;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public enum NotificationWorkKind { Generate, SignalR, Push, StateChanged, PreferencesChanged }

public sealed class NotificationWork
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public NotificationWorkKind Kind { get; set; }
    public Guid UserId { get; set; }
    public string SourceEventKey { get; set; } = string.Empty;
    public NotificationType Type { get; set; }
    public string? ActivityKind { get; set; }
    public Guid? ActorUserId { get; set; }
    public Guid? BoardId { get; set; }
    public Guid? MembershipInstanceId { get; set; }
    public string? ResourceKind { get; set; }
    public Guid? ResourceId { get; set; }
    public long? MessageSequence { get; set; }
    public Guid? NotificationId { get; set; }
    public long? NotificationRevision { get; set; }
    public Guid? PushSubscriptionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public int Attempts { get; set; }
}

public sealed class NotificationWorkConfiguration : IEntityTypeConfiguration<NotificationWork>
{
    public void Configure(EntityTypeBuilder<NotificationWork> entity)
    {
        entity.ToTable("notification_work", table =>
        {
            table.HasCheckConstraint("ck_notification_work_kind", "kind BETWEEN 0 AND 4");
            table.HasCheckConstraint("ck_notification_work_attempts", "attempts >= 0");
        });
        entity.HasKey(item => item.Id);
        entity.Property(item => item.SourceEventKey).HasMaxLength(200).IsRequired();
        entity.Property(item => item.ActivityKind).HasMaxLength(50);
        entity.Property(item => item.ResourceKind).HasMaxLength(50);
        entity.HasIndex(item => new { item.UserId, item.SourceEventKey, item.Kind }).IsUnique();
        entity.HasIndex(item => new { item.NextAttemptAt, item.CreatedAt, item.Id })
            .HasFilter("processed_at IS NULL");
    }
}

public sealed class NotificationClientPresence
{
    public Guid UserId { get; set; }
    public Guid InstallationId { get; set; }
    public Guid TabId { get; set; }
    public Guid? BoardId { get; set; }
    public bool ChatVisible { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class NotificationClientPresenceConfiguration : IEntityTypeConfiguration<NotificationClientPresence>
{
    public void Configure(EntityTypeBuilder<NotificationClientPresence> entity)
    {
        entity.ToTable("notification_client_presence");
        entity.HasKey(item => new { item.UserId, item.InstallationId, item.TabId });
        entity.HasIndex(item => item.ExpiresAt);
    }
}
