namespace Wukna.Features.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class PersonalTaskListConfiguration : IEntityTypeConfiguration<PersonalTaskList>
{
    public void Configure(EntityTypeBuilder<PersonalTaskList> entity)
    {
        entity.ToTable("personal_task_lists");
        entity.HasKey(list => list.Id);
        entity.Property(list => list.Name).HasMaxLength(80).IsRequired();
        entity.Property(list => list.NormalizedName).HasMaxLength(80).IsRequired();
        entity.Property(list => list.CreatedAt).HasDefaultValueSql("now()");
        entity.Property(list => list.UpdatedAt).HasDefaultValueSql("now()");
        entity.HasOne(list => list.User).WithMany()
            .HasForeignKey(list => list.UserId).OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(list => new { list.UserId, list.NormalizedName })
            .IsUnique().HasDatabaseName("ux_personal_task_lists_user_name");
    }
}
