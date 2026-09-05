namespace OrderService.Application.Orders;

public sealed record OrderDto(
    Guid Id,
    Guid UserId,
    IReadOnlyCollection<OrderItemDto> Items,
    decimal TotalAmount,
    DateTimeOffset CreatedAtUtc);
