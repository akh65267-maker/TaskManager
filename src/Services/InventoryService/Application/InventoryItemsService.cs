using InventoryService.Application.Inventory;
using InventoryService.Domain;

namespace InventoryService.Application;

public class InventoryItemsService
{
    private readonly IInventoryRepository _repo;
    private readonly ILogger<InventoryItemsService> _logger;

    public InventoryItemsService(IInventoryRepository repo, ILogger<InventoryItemsService> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<InventoryItemDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var items = await _repo.GetAllAsync(cancellationToken);

        return items.Select(i => new InventoryItemDto(i.ProductId, i.QuantityAvailable)).ToList();
    }

    public async Task<InventoryItemDto?> GetByProductIdAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        var item = await _repo.GetByProductIdAsync(productId, cancellationToken);

        return item is null ? null : new InventoryItemDto(item.ProductId, item.QuantityAvailable);
    }

    public async Task CreateAsync(CreateInventoryItemRequest request, CancellationToken cancellationToken = default)
    {
        var item = new InventoryItem(request.ProductId, request.QuantityAvailable);

        if (await _repo.ExistsAsync(item.ProductId, cancellationToken))
            throw new ArgumentException($"Inventory for product '{item.ProductId}' already exists.", nameof(request));

        await _repo.AddAsync(item, cancellationToken);
        await _repo.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Created inventory for product {ProductId} with quantity {Quantity}",
            item.ProductId,
            item.QuantityAvailable);
    }

    public async Task<InventoryItemDto?> RestockAsync(Guid productId, int quantity, CancellationToken cancellationToken = default)
    {
        // Read-modify-write under the row lock, inside a transaction. A plain read here lets a
        // reservation that commits between the read and the write be overwritten: stock then
        // rises by the restock plus whatever that reservation had taken. The reserve and
        // release consumers take the same lock, so all three writers queue behind each other.
        var item = await _repo.InTransactionAsync(async () =>
        {
            var locked = await _repo.GetByProductIdForUpdateAsync(productId, cancellationToken);
            if (locked is null)
                return null;

            locked.Restock(quantity);
            await _repo.SaveChangesAsync(cancellationToken);
            return locked;
        }, cancellationToken);

        if (item is null)
            return null;

        _logger.LogInformation(
            "Restocked product {ProductId} by {Quantity}, now {NewQuantity}",
            item.ProductId,
            quantity,
            item.QuantityAvailable);

        return new InventoryItemDto(item.ProductId, item.QuantityAvailable);
    }
}
