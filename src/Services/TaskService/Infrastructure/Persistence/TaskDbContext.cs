using MassTransit;
using Microsoft.EntityFrameworkCore;
using TaskService.Domain;

namespace TaskService.Infrastructure.Persistence;

public class TaskDbContext : DbContext
{
    public TaskDbContext(DbContextOptions<TaskDbContext> options) : base(options)
    {
    }

    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<KnownUser> KnownUsers => Set<KnownUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TaskItem>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Title)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(x => x.IsCompleted)
                .IsRequired();

            entity.Property(x => x.OwnerId)
                .IsRequired();
        });

        modelBuilder.Entity<KnownUser>(entity =>
        {
            entity.HasKey(x => x.UserId);

            entity.Property(x => x.Email)
                .IsRequired()
                .HasMaxLength(320);
        });

        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
    }
}
