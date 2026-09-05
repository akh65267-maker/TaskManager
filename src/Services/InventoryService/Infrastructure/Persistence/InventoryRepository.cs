using InventoryService.Domain;
using Microsoft.EntityFrameworkCore;

namespace InventoryService.Infrastructure.Persistence;

public class InventoryRepository : IInventoryRepository
{
    private readonly InventoryDbContext _db;

    public InventoryRepository(InventoryDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyCollection<InventoryItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _db.InventoryItems
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<InventoryItem?> GetByProductIdAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        return await _db.InventoryItems
            .FirstOrDefaultAsync(x => x.ProductId == productId, cancellationToken);
    }

    public async Task<bool> ExistsAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        return await _db.InventoryItems
            .AsNoTracking()
            .AnyAsync(x => x.ProductId == productId, cancellationToken);
    }

    public async Task AddAsync(InventoryItem item, CancellationToken cancellationToken = default)
    {
        await _db.InventoryItems.AddAsync(item, cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return _db.SaveChangesAsync(cancellationToken);
    }
}
