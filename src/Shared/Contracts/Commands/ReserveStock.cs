namespace Contracts.Commands;

public sealed record ReserveStock(Guid OrderId, Guid ProductId, int Quantity);
