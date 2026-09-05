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
}
