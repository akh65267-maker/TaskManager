using BasketService.Domain;

namespace BasketService.UnitTests.Domain;

public class BasketItemTests
{
    [Fact]
    public void Constructor_WithEmptyProductId_Throws()
    {
        Assert.Throws<ArgumentException>(() => new BasketItem(Guid.Empty, 1));
    }

    [Fact]
    public void Constructor_WithNonPositiveQuantity_Throws()
    {
        Assert.Throws<ArgumentException>(() => new BasketItem(Guid.NewGuid(), 0));
    }

    [Fact]
    public void IncreaseQuantity_AddsToExistingQuantity()
    {
        var item = new BasketItem(Guid.NewGuid(), 2);

        item.IncreaseQuantity(3);

        Assert.Equal(5, item.Quantity);
    }

    [Fact]
    public void IncreaseQuantity_WithNonPositiveAmount_Throws()
    {
        var item = new BasketItem(Guid.NewGuid(), 2);

        Assert.Throws<ArgumentException>(() => item.IncreaseQuantity(0));
    }
}
