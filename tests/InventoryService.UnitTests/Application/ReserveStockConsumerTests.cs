using Contracts.Commands;
using Contracts.IntegrationEvents;
using InventoryService.Application.Consumers;
using InventoryService.Domain;
using MassTransit;
using Microsoft.Extensions.Logging;
using Moq;

namespace InventoryService.UnitTests.Application;

public class ReserveStockConsumerTests
{
    private readonly Mock<IInventoryRepository> _repo = new();
    private readonly ReserveStockConsumer _sut;

    public ReserveStockConsumerTests()
    {
        _sut = new ReserveStockConsumer(_repo.Object, Mock.Of<ILogger<ReserveStockConsumer>>());
    }

    private static Mock<ConsumeContext<ReserveStock>> CreateContext(ReserveStock message)
    {
        var context = new Mock<ConsumeContext<ReserveStock>>();
        context.SetupGet(x => x.Message).Returns(message);
        context.SetupGet(x => x.CancellationToken).Returns(CancellationToken.None);
        return context;
    }

    [Fact]
    public async Task Consume_SufficientStock_PublishesStockReservedBeforeSaving()
    {
        var sequence = new MockSequence();
        var productId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var item = new InventoryItem(productId, 10);
        var message = new ReserveStock(orderId, productId, 4);
        var context = CreateContext(message);

        _repo.Setup(x => x.GetByProductIdAsync(productId, It.IsAny<CancellationToken>())).ReturnsAsync(item);
        context.InSequence(sequence)
            .Setup(x => x.Publish(It.IsAny<StockReserved>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.InSequence(sequence)
            .Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _sut.Consume(context.Object);

        Assert.Equal(6, item.QuantityAvailable);
        context.Verify(x => x.Publish(
            It.Is<StockReserved>(e => e.OrderId == orderId && e.ProductId == productId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consume_InsufficientStock_PublishesFailureAndSaves()
    {
        var productId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var item = new InventoryItem(productId, 2);
        var message = new ReserveStock(orderId, productId, 5);
        var context = CreateContext(message);

        _repo.Setup(x => x.GetByProductIdAsync(productId, It.IsAny<CancellationToken>())).ReturnsAsync(item);

        await _sut.Consume(context.Object);

        Assert.Equal(2, item.QuantityAvailable);
        context.Verify(x => x.Publish(
            It.Is<StockReservationFailed>(e => e.OrderId == orderId && e.ProductId == productId),
            It.IsAny<CancellationToken>()), Times.Once);
        _repo.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consume_UnknownProduct_PublishesFailureAndSaves()
    {
        var productId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var message = new ReserveStock(orderId, productId, 1);
        var context = CreateContext(message);

        _repo.Setup(x => x.GetByProductIdAsync(productId, It.IsAny<CancellationToken>())).ReturnsAsync((InventoryItem?)null);

        await _sut.Consume(context.Object);

        context.Verify(x => x.Publish(
            It.Is<StockReservationFailed>(e => e.OrderId == orderId && e.ProductId == productId),
            It.IsAny<CancellationToken>()), Times.Once);
        _repo.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
