namespace Contracts.IntegrationEvents;

public sealed record StockReserved(Guid OrderId, Guid ProductId);
