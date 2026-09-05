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

    [Fact]
    public void Reserve_WithSufficientStock_DecreasesQuantityAvailable()
    {
        var item = new InventoryItem(Guid.NewGuid(), 10);

        item.Reserve(4);

        Assert.Equal(6, item.QuantityAvailable);
    }

    [Fact]
    public void Reserve_ExactlyAllAvailable_ReducesToZero()
    {
        var item = new InventoryItem(Guid.NewGuid(), 5);

        item.Reserve(5);

        Assert.Equal(0, item.QuantityAvailable);
    }

    [Fact]
    public void Reserve_MoreThanAvailable_ThrowsInsufficientStock()
    {
        var item = new InventoryItem(Guid.NewGuid(), 3);

        Assert.Throws<InsufficientStockException>(() => item.Reserve(4));
        Assert.Equal(3, item.QuantityAvailable);
    }

    [Fact]
    public void Reserve_WithNonPositiveQuantity_Throws()
    {
        var item = new InventoryItem(Guid.NewGuid(), 10);

        Assert.Throws<ArgumentException>(() => item.Reserve(0));
    }

    [Fact]
    public void Release_IncreasesQuantityAvailable()
    {
        var item = new InventoryItem(Guid.NewGuid(), 10);
        item.Reserve(4);

        item.Release(4);

        Assert.Equal(10, item.QuantityAvailable);
    }

    [Fact]
    public void Release_WithNonPositiveQuantity_Throws()
    {
        var item = new InventoryItem(Guid.NewGuid(), 10);

        Assert.Throws<ArgumentException>(() => item.Release(0));
    }
}
