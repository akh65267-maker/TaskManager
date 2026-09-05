namespace OrderService.Application.Orders;

public sealed record CreateOrderRequest(IReadOnlyCollection<OrderItemRequest> Items);
