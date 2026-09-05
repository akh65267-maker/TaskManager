using System.Text.Json.Serialization;

namespace BasketService.Domain;

public class BasketItem
{
    public Guid ProductId { get; private set; }
    public int Quantity { get; private set; }

    [JsonConstructor]
    public BasketItem(Guid productId, int quantity)
    {
        if (productId == Guid.Empty)
            throw new ArgumentException("Product id is required.", nameof(productId));

        if (quantity <= 0)
            throw new ArgumentException("Quantity must be positive.", nameof(quantity));

        ProductId = productId;
        Quantity = quantity;
    }

    public void IncreaseQuantity(int amount)
    {
        if (amount <= 0)
            throw new ArgumentException("Amount must be positive.", nameof(amount));

        Quantity += amount;
    }
}
