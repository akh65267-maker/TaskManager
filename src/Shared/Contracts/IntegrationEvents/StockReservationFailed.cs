namespace Contracts.IntegrationEvents;

public sealed record StockReservationFailed(Guid OrderId, Guid ProductId, string Reason);
