namespace OrderService.Application.Orders;

public sealed record OrderItemDto(Guid ProductId, int Quantity, decimal UnitPrice);
