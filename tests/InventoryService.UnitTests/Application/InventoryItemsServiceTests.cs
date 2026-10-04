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
        // The transaction itself is the repository's concern; here it just runs the work.
        _repo.Setup(x => x.InTransactionAsync(It.IsAny<Func<Task<InventoryItem?>>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<Task<InventoryItem?>> work, CancellationToken _) => work());

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

    [Fact]
    public async Task RestockAsync_UnknownProductId_ReturnsNullWithoutSaving()
    {
        _repo.Setup(x => x.GetByProductIdForUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InventoryItem?)null);

        var result = await _sut.RestockAsync(Guid.NewGuid(), 10);

        Assert.Null(result);
        _repo.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RestockAsync_KnownProductId_IncreasesQuantityAndSaves()
    {
        var productId = Guid.NewGuid();
        var item = new InventoryItem(productId, 5);
        _repo.Setup(x => x.GetByProductIdForUpdateAsync(productId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);

        var result = await _sut.RestockAsync(productId, 10);

        Assert.NotNull(result);
        Assert.Equal(15, result!.QuantityAvailable);
        _repo.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RestockAsync_ReadsUnderTheRowLockInsideATransaction_NotWithAPlainRead()
    {
        // The lost-update regression in miniature: a plain read has no lock, and a lock outside
        // a transaction is released the moment the SELECT ends.
        var productId = Guid.NewGuid();
        _repo.Setup(x => x.GetByProductIdForUpdateAsync(productId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InventoryItem(productId, 5));

        await _sut.RestockAsync(productId, 10);

        _repo.Verify(x => x.InTransactionAsync(It.IsAny<Func<Task<InventoryItem?>>>(), It.IsAny<CancellationToken>()), Times.Once);
        _repo.Verify(x => x.GetByProductIdForUpdateAsync(productId, It.IsAny<CancellationToken>()), Times.Once);
        _repo.Verify(x => x.GetByProductIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
