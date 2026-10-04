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

    public async Task<InventoryItem?> GetByProductIdForUpdateAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        // EF has no FOR UPDATE, so this is raw SQL. ToListAsync rather than
        // FirstOrDefaultAsync on purpose: composing LINQ over FromSql wraps the statement
        // in a subquery, and the lock is only meant to be taken by this exact statement.
        // The row is returned tracked, so Reserve/Release/Restock and SaveChanges work
        // exactly as they did after GetByProductIdAsync.
        var rows = await _db.InventoryItems
            .FromSqlInterpolated($"SELECT * FROM \"InventoryItems\" WHERE \"ProductId\" = {productId} FOR UPDATE")
            .ToListAsync(cancellationToken);

        return rows.SingleOrDefault();
    }

    public async Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken = default)
    {
        // No execution strategy to wrap this in: Program.cs deliberately does not enable
        // EnableRetryOnFailure, which does not support user-initiated transactions.
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var result = await work();

        await transaction.CommitAsync(cancellationToken);
        return result;
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
