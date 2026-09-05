namespace Contracts.Common;

public sealed record OrderLineItem(Guid ProductId, int Quantity, decimal UnitPrice);
