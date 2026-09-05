using OrderService.Application.Orders;
using OrderService.Domain;

namespace OrderService.Application;

public class OrdersService
{
    private readonly IOrderRepository _repo;
    private readonly ILogger<OrdersService> _logger;

    public OrdersService(IOrderRepository repo, ILogger<OrdersService> logger)
    {
        _repo = repo;
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
            order.CreatedAtUtc);
    }
}
