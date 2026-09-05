namespace InventoryService.Domain;

public class InsufficientStockException : Exception
{
    public InsufficientStockException(Guid productId, int requested, int available)
        : base($"Insufficient stock for product '{productId}': requested {requested}, available {available}.")
    {
    }
}
