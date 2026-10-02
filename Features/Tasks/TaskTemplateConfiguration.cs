namespace Wukna.Features.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class TaskTemplateConfiguration : IEntityTypeConfiguration<TaskTemplate>
{
    public void Configure(EntityTypeBuilder<TaskTemplate> entity)
    {
        entity.ToTable("task_templates");
        entity.HasKey(template => template.Id);
        entity.Property(template => template.Name).HasMaxLength(80).IsRequired();
        entity.Property(template => template.ItemsJson).HasColumnType("text").IsRequired();
        entity.HasOne(template => template.User).WithMany()
            .HasForeignKey(template => template.UserId).OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(template => template.UserId);
    }
}
