using OrderService.Domain;

namespace OrderService.UnitTests.Domain;

public class OrderTests
{
    [Fact]
    public void Constructor_WithEmptyUserId_Throws()
    {
        var items = new List<OrderItem> { new(Guid.NewGuid(), 1, 9.99m) };

        Assert.Throws<ArgumentException>(() => new Order(Guid.Empty, items));
    }

    [Fact]
    public void Constructor_WithNoItems_Throws()
    {
        Assert.Throws<ArgumentException>(() => new Order(Guid.NewGuid(), new List<OrderItem>()));
    }

    [Fact]
    public void Constructor_WithNullItems_Throws()
    {
        Assert.Throws<ArgumentException>(() => new Order(Guid.NewGuid(), null!));
    }

    [Fact]
    public void TotalAmount_SumsQuantityTimesUnitPriceAcrossItems()
    {
        var items = new List<OrderItem>
        {
            new(Guid.NewGuid(), 2, 10m),
            new(Guid.NewGuid(), 3, 5m),
        };

        var order = new Order(Guid.NewGuid(), items);

        Assert.Equal(35m, order.TotalAmount);
    }

    [Fact]
    public void Constructor_WithValidArguments_SetsIdAndCreatedAtUtc()
    {
        var items = new List<OrderItem> { new(Guid.NewGuid(), 1, 9.99m) };

        var order = new Order(Guid.NewGuid(), items);

        Assert.NotEqual(Guid.Empty, order.Id);
        Assert.True(order.CreatedAtUtc <= DateTimeOffset.UtcNow);
    }
}
