using Microsoft.EntityFrameworkCore;
using OrderService.Domain;

namespace OrderService.Infrastructure.Persistence;

public class OrderDbContext : DbContext
{
    public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options)
    {
    }

    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.UserId)
                .IsRequired();

            entity.Property(x => x.Status)
                .IsRequired()
                .HasConversion<string>()
                .HasMaxLength(20);

            entity.Property(x => x.CreatedAtUtc)
                .IsRequired();

            entity.Ignore(x => x.TotalAmount);

            entity.OwnsMany(x => x.Items, item =>
            {
                item.WithOwner().HasForeignKey("OrderId");
                item.Property<Guid>("Id");
                item.HasKey("Id");

                item.Property(i => i.ProductId).IsRequired();
                item.Property(i => i.Quantity).IsRequired();
                item.Property(i => i.UnitPrice).IsRequired().HasPrecision(18, 2);
            });
        });
    }
}
