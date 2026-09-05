using Contracts.Common;

namespace Contracts.IntegrationEvents;

public sealed record OrderSubmitted(Guid OrderId, Guid UserId, IReadOnlyCollection<OrderLineItem> Items);
