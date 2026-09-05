using BasketService.Application;
using BasketService.Application.Baskets;
using BasketService.Domain;
using Microsoft.Extensions.Logging;
using Moq;

namespace BasketService.UnitTests.Application;

public class BasketsServiceTests
{
    private readonly Mock<IBasketRepository> _repo = new();
    private readonly BasketsService _sut;

    public BasketsServiceTests()
    {
        _sut = new BasketsService(_repo.Object, Mock.Of<ILogger<BasketsService>>());
    }

    [Fact]
    public async Task GetAsync_NoExistingBasket_ReturnsEmptyBasketWithoutSaving()
    {
        var userId = Guid.NewGuid();
        _repo.Setup(x => x.GetAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync((Basket?)null);

        var result = await _sut.GetAsync(userId);

        Assert.Equal(userId, result.UserId);
        Assert.Empty(result.Items);
        _repo.Verify(x => x.SaveAsync(It.IsAny<Basket>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddItemAsync_NoExistingBasket_CreatesOneAndSaves()
    {
        var userId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        _repo.Setup(x => x.GetAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync((Basket?)null);

        var result = await _sut.AddItemAsync(userId, new AddBasketItemRequest(productId, 2));

        Assert.Equal(userId, result.UserId);
        var item = Assert.Single(result.Items);
        Assert.Equal(productId, item.ProductId);
        Assert.Equal(2, item.Quantity);
        _repo.Verify(x => x.SaveAsync(
            It.Is<Basket>(b => b.UserId == userId && b.Items.Count == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddItemAsync_ExistingBasket_MergesIntoIt()
    {
        var userId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var existing = new Basket(userId);
        existing.AddItem(productId, 1);
        _repo.Setup(x => x.GetAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var result = await _sut.AddItemAsync(userId, new AddBasketItemRequest(productId, 4));

        var item = Assert.Single(result.Items);
        Assert.Equal(5, item.Quantity);
    }

    [Fact]
    public async Task RemoveItemAsync_RemovesItemAndSaves()
    {
        var userId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var existing = new Basket(userId);
        existing.AddItem(productId, 1);
        _repo.Setup(x => x.GetAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var result = await _sut.RemoveItemAsync(userId, productId);

        Assert.Empty(result.Items);
        _repo.Verify(x => x.SaveAsync(It.IsAny<Basket>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ClearAsync_DeletesBasket()
    {
        var userId = Guid.NewGuid();

        await _sut.ClearAsync(userId);

        _repo.Verify(x => x.DeleteAsync(userId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
