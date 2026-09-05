using InventoryService.Application;
using InventoryService.Application.Inventory;
using InventoryService.Domain;
using Microsoft.Extensions.Logging;
using Moq;

namespace InventoryService.UnitTests.Application;

public class InventoryItemsServiceTests
{
    private readonly Mock<IInventoryRepository> _repo = new();
    private readonly InventoryItemsService _sut;

    public InventoryItemsServiceTests()
    {
        _sut = new InventoryItemsService(_repo.Object, Mock.Of<ILogger<InventoryItemsService>>());
    }

    [Fact]
    public async Task CreateAsync_WithExistingProductId_ThrowsWithoutAdding()
    {
        var productId = Guid.NewGuid();
        _repo.Setup(x => x.ExistsAsync(productId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.CreateAsync(new CreateInventoryItemRequest(productId, 10)));

        _repo.Verify(x => x.AddAsync(It.IsAny<InventoryItem>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_WithNewProductId_AddsAndSaves()
    {
        var productId = Guid.NewGuid();
        _repo.Setup(x => x.ExistsAsync(productId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await _sut.CreateAsync(new CreateInventoryItemRequest(productId, 10));

        _repo.Verify(x => x.AddAsync(
            It.Is<InventoryItem>(i => i.ProductId == productId && i.QuantityAvailable == 10),
            It.IsAny<CancellationToken>()), Times.Once);
        _repo.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetByProductIdAsync_NotFound_ReturnsNull()
    {
        _repo.Setup(x => x.GetByProductIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InventoryItem?)null);

        var result = await _sut.GetByProductIdAsync(Guid.NewGuid());

        Assert.Null(result);
    }
}
