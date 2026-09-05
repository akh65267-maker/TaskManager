namespace InventoryService.Domain;

public interface IInventoryRepository
{
    Task<IReadOnlyCollection<InventoryItem>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<InventoryItem?> GetByProductIdAsync(Guid productId, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(Guid productId, CancellationToken cancellationToken = default);
    Task AddAsync(InventoryItem item, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
