namespace InventoryService.Application.Inventory;

public sealed record InventoryItemDto(Guid ProductId, int QuantityAvailable);
