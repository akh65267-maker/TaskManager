using Contracts.Common;
using Contracts.IntegrationEvents;
using MassTransit;
using OrderService.Application.Orders;
using OrderService.Domain;

namespace OrderService.Application;

public class OrdersService
{
    private readonly IOrderRepository _repo;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<OrdersService> _logger;

    public OrdersService(IOrderRepository repo, IPublishEndpoint publishEndpoint, ILogger<OrdersService> logger)
    {
        _repo = repo;
        _publishEndpoint = publishEndpoint;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<OrderDto>> GetAllAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var orders = await _repo.GetAllByUserIdAsync(userId, cancellationToken);

        return orders.Select(ToDto).ToList();
    }

    public async Task<OrderDto?> GetByIdAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        var order = await _repo.GetByIdAsync(id, cancellationToken);

        return order is null || order.UserId != userId ? null : ToDto(order);
    }

    public async Task<Guid> CreateAsync(CreateOrderRequest request, Guid userId, CancellationToken cancellationToken = default)
    {
        var items = request.Items
            .Select(i => new OrderItem(i.ProductId, i.Quantity, i.UnitPrice))
            .ToList();

        var order = new Order(userId, items);

        await _repo.AddAsync(order, cancellationToken);

        await _publishEndpoint.Publish(
            new OrderSubmitted(
                order.Id,
                userId,
                order.Items.Select(i => new OrderLineItem(i.ProductId, i.Quantity, i.UnitPrice)).ToList()),
            cancellationToken);

        // Single SaveChangesAsync commits the order row and the buffered
        // OrderSubmitted outbox message atomically, same pattern (and same
        // reason) as UserService.RegisterAsync: publish before this call,
        // never after, or the message never gets a SaveChanges to flush into.
        await _repo.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Created order {OrderId} for user {UserId} with {ItemCount} item(s), total {TotalAmount}",
            order.Id,
            userId,
            order.Items.Count,
            order.TotalAmount);

        return order.Id;
    }

    private static OrderDto ToDto(Order order)
    {
        return new OrderDto(
            order.Id,
            order.UserId,
            order.Items.Select(i => new OrderItemDto(i.ProductId, i.Quantity, i.UnitPrice)).ToList(),
            order.TotalAmount,
            order.Status,
            order.CreatedAtUtc);
    }
}
