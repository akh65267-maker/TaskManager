namespace InventoryService.Domain;

public interface IInventoryRepository
{
    Task<IReadOnlyCollection<InventoryItem>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<InventoryItem?> GetByProductIdAsync(Guid productId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the item holding a row lock until the transaction ends. Every read-modify-write
    /// (reserve, release, restock) must use this rather than GetByProductIdAsync: without the
    /// lock, two concurrent writers both read the same quantity and one silently overwrites
    /// the other, or - under a strict isolation level - one fails and has to be retried.
    /// </summary>
    Task<InventoryItem?> GetByProductIdForUpdateAsync(Guid productId, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(Guid productId, CancellationToken cancellationToken = default);
    Task AddAsync(InventoryItem item, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
