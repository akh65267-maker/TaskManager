using InventoryService.Domain;

namespace InventoryService.UnitTests.Domain;

public class InventoryItemTests
{
    [Fact]
    public void Constructor_WithEmptyProductId_Throws()
    {
        Assert.Throws<ArgumentException>(() => new InventoryItem(Guid.Empty, 10));
    }

    [Fact]
    public void Constructor_WithNegativeQuantity_Throws()
    {
        Assert.Throws<ArgumentException>(() => new InventoryItem(Guid.NewGuid(), -1));
    }

    [Fact]
    public void Constructor_WithValidArguments_SetsProperties()
    {
        var productId = Guid.NewGuid();
        var item = new InventoryItem(productId, 10);

        Assert.Equal(productId, item.ProductId);
        Assert.Equal(10, item.QuantityAvailable);
    }
}
