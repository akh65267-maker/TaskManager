namespace InventoryService.Domain;

public class InventoryItem
{
    public Guid ProductId { get; private set; }
    public int QuantityAvailable { get; private set; }

    private InventoryItem()
    {
    }

    public InventoryItem(Guid productId, int quantityAvailable)
    {
        if (productId == Guid.Empty)
            throw new ArgumentException("Product id is required.", nameof(productId));

        if (quantityAvailable < 0)
            throw new ArgumentException("Quantity cannot be negative.", nameof(quantityAvailable));

        ProductId = productId;
        QuantityAvailable = quantityAvailable;
    }

    public void Reserve(int quantity)
    {
        if (quantity <= 0)
            throw new ArgumentException("Quantity must be positive.", nameof(quantity));

        if (quantity > QuantityAvailable)
            throw new InsufficientStockException(ProductId, quantity, QuantityAvailable);

        QuantityAvailable -= quantity;
    }

    public void Release(int quantity)
    {
        if (quantity <= 0)
            throw new ArgumentException("Quantity must be positive.", nameof(quantity));

        QuantityAvailable += quantity;
    }
}
