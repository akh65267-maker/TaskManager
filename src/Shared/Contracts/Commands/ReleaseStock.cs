namespace Contracts.Commands;

public sealed record ReleaseStock(Guid OrderId, Guid ProductId, int Quantity);
