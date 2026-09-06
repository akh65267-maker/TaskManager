using InventoryService.Domain;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace InventoryService.Infrastructure.Persistence;

public class InventoryDbContext : DbContext
{
    public InventoryDbContext(DbContextOptions<InventoryDbContext> options) : base(options)
    {
    }

    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InventoryItem>(entity =>
        {
            entity.HasKey(x => x.ProductId);

            entity.Property(x => x.QuantityAvailable)
                .IsRequired();
        });

        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
    }
}
