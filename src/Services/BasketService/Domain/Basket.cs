using System.Text.Json.Serialization;

namespace BasketService.Domain;

/// <summary>
/// One basket per user, stored as a single JSON blob in Redis (keyed by
/// UserId) rather than mapped by EF Core — there's no relational store here.
/// </summary>
public class Basket
{
    public Guid UserId { get; private set; }
    public List<BasketItem> Items { get; private set; }

    [JsonConstructor]
    public Basket(Guid userId, List<BasketItem> items)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("User id is required.", nameof(userId));

        UserId = userId;
        Items = items ?? new List<BasketItem>();
    }

    public Basket(Guid userId) : this(userId, new List<BasketItem>())
    {
    }

    public void AddItem(Guid productId, int quantity)
    {
        var existing = Items.FirstOrDefault(i => i.ProductId == productId);

        if (existing is not null)
            existing.IncreaseQuantity(quantity);
        else
            Items.Add(new BasketItem(productId, quantity));
    }

    public void RemoveItem(Guid productId)
    {
        Items.RemoveAll(i => i.ProductId == productId);
    }
}
