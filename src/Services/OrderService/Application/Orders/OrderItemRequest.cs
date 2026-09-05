namespace OrderService.Application.Orders;

public sealed record OrderItemRequest(Guid ProductId, int Quantity, decimal UnitPrice);
