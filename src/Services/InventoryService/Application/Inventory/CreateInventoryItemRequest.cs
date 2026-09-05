namespace InventoryService.Application.Inventory;

public sealed record CreateInventoryItemRequest(Guid ProductId, int QuantityAvailable);
