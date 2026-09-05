using BasketService.Domain;

namespace BasketService.UnitTests.Domain;

public class BasketTests
{
    [Fact]
    public void Constructor_WithEmptyUserId_Throws()
    {
        Assert.Throws<ArgumentException>(() => new Basket(Guid.Empty));
    }

    [Fact]
    public void Constructor_ForNewUser_StartsWithNoItems()
    {
        var basket = new Basket(Guid.NewGuid());

        Assert.Empty(basket.Items);
    }

    [Fact]
    public void AddItem_NewProduct_AddsItem()
    {
        var basket = new Basket(Guid.NewGuid());
        var productId = Guid.NewGuid();

        basket.AddItem(productId, 2);

        var item = Assert.Single(basket.Items);
        Assert.Equal(productId, item.ProductId);
        Assert.Equal(2, item.Quantity);
    }

    [Fact]
    public void AddItem_ExistingProduct_IncreasesQuantityInsteadOfDuplicating()
    {
        var basket = new Basket(Guid.NewGuid());
        var productId = Guid.NewGuid();

        basket.AddItem(productId, 2);
        basket.AddItem(productId, 3);

        var item = Assert.Single(basket.Items);
        Assert.Equal(5, item.Quantity);
    }

    [Fact]
    public void RemoveItem_ExistingProduct_RemovesIt()
    {
        var basket = new Basket(Guid.NewGuid());
        var productId = Guid.NewGuid();
        basket.AddItem(productId, 2);

        basket.RemoveItem(productId);

        Assert.Empty(basket.Items);
    }

    [Fact]
    public void RemoveItem_UnknownProduct_DoesNothing()
    {
        var basket = new Basket(Guid.NewGuid());
        basket.AddItem(Guid.NewGuid(), 2);

        basket.RemoveItem(Guid.NewGuid());

        Assert.Single(basket.Items);
    }
}
