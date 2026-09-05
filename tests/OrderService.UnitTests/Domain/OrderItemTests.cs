using OrderService.Domain;

namespace OrderService.UnitTests.Domain;

public class OrderItemTests
{
    [Fact]
    public void Constructor_WithEmptyProductId_Throws()
    {
        Assert.Throws<ArgumentException>(() => new OrderItem(Guid.Empty, 1, 9.99m));
    }

    [Fact]
    public void Constructor_WithNonPositiveQuantity_Throws()
    {
        Assert.Throws<ArgumentException>(() => new OrderItem(Guid.NewGuid(), 0, 9.99m));
    }

    [Fact]
    public void Constructor_WithNegativeUnitPrice_Throws()
    {
        Assert.Throws<ArgumentException>(() => new OrderItem(Guid.NewGuid(), 1, -1m));
    }

    [Fact]
    public void Constructor_WithValidArguments_SetsProperties()
    {
        var productId = Guid.NewGuid();
        var item = new OrderItem(productId, 3, 9.99m);

        Assert.Equal(productId, item.ProductId);
        Assert.Equal(3, item.Quantity);
        Assert.Equal(9.99m, item.UnitPrice);
    }
}
