using Contracts.IntegrationEvents;
using MassTransit;
using Microsoft.Extensions.Logging;
using Moq;
using OrderService.Application;
using OrderService.Application.Orders;
using OrderService.Domain;

namespace OrderService.UnitTests.Application;

public class OrdersServiceTests
{
    private readonly Mock<IOrderRepository> _repo = new();
    private readonly Mock<IPublishEndpoint> _publishEndpoint = new();
    private readonly OrdersService _sut;

    public OrdersServiceTests()
    {
        _sut = new OrdersService(_repo.Object, _publishEndpoint.Object, Mock.Of<ILogger<OrdersService>>());
    }

    [Fact]
    public async Task CreateAsync_WithNoItems_ThrowsWithoutAdding()
    {
        var userId = Guid.NewGuid();
        var request = new CreateOrderRequest(Array.Empty<OrderItemRequest>());

        await Assert.ThrowsAsync<ArgumentException>(() => _sut.CreateAsync(request, userId));

        _repo.Verify(x => x.AddAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_WithValidRequest_AddsAndSaves()
    {
        var userId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var request = new CreateOrderRequest(new[] { new OrderItemRequest(productId, 2, 10m) });

        var id = await _sut.CreateAsync(request, userId);

        Assert.NotEqual(Guid.Empty, id);
        _repo.Verify(x => x.AddAsync(
            It.Is<Order>(o => o.UserId == userId && o.TotalAmount == 20m),
            It.IsAny<CancellationToken>()), Times.Once);
        _repo.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_WithValidRequest_PublishesOrderSubmittedBeforeSaving()
    {
        var sequence = new MockSequence();
        var userId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var request = new CreateOrderRequest(new[] { new OrderItemRequest(productId, 2, 10m) });

        _repo.InSequence(sequence).Setup(x => x.AddAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _publishEndpoint.InSequence(sequence).Setup(x => x.Publish(It.IsAny<OrderSubmitted>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.InSequence(sequence).Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _sut.CreateAsync(request, userId);

        _publishEndpoint.Verify(x => x.Publish(
            It.Is<OrderSubmitted>(e => e.UserId == userId && e.Items.Count == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetByIdAsync_OwnedByDifferentUser_ReturnsNull()
    {
        var order = new Order(Guid.NewGuid(), new List<OrderItem> { new(Guid.NewGuid(), 1, 9.99m) });
        _repo.Setup(x => x.GetByIdAsync(order.Id, It.IsAny<CancellationToken>())).ReturnsAsync(order);

        var result = await _sut.GetByIdAsync(order.Id, Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetByIdAsync_NotFound_ReturnsNull()
    {
        _repo.Setup(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Order?)null);

        var result = await _sut.GetByIdAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.Null(result);
    }
}
