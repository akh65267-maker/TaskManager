namespace OrderService.Domain;

public class Order
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public List<OrderItem> Items { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public decimal TotalAmount => Items.Sum(i => i.Quantity * i.UnitPrice);

    private Order()
    {
    }

    public Order(Guid userId, List<OrderItem> items)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("User id is required.", nameof(userId));

        if (items is null || items.Count == 0)
            throw new ArgumentException("An order must have at least one item.", nameof(items));

        Id = Guid.NewGuid();
        UserId = userId;
        Items = items;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }
}
