using MassTransit;
using Microsoft.EntityFrameworkCore;
using UserService.Domain;

namespace UserService.Infrastructure.Persistence;

public class UserDbContext : DbContext
{
    public UserDbContext(DbContextOptions<UserDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Email)
                .IsRequired()
                .HasMaxLength(320);

            entity.HasIndex(x => x.Email).IsUnique();

            entity.Property(x => x.DisplayName)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(x => x.Role)
                .IsRequired()
                .HasConversion<string>()
                .HasMaxLength(20);
        });

        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
