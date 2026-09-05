namespace BasketService.Application.Baskets;

public sealed record BasketDto(Guid UserId, IReadOnlyCollection<BasketItemDto> Items);
