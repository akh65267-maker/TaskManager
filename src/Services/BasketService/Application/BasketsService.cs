using BasketService.Application.Baskets;
using BasketService.Domain;

namespace BasketService.Application;

public class BasketsService
{
    private readonly IBasketRepository _repo;
    private readonly ILogger<BasketsService> _logger;

    public BasketsService(IBasketRepository repo, ILogger<BasketsService> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task<BasketDto> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var basket = await _repo.GetAsync(userId, cancellationToken) ?? new Basket(userId);

        return ToDto(basket);
    }

    public async Task<BasketDto> AddItemAsync(Guid userId, AddBasketItemRequest request, CancellationToken cancellationToken = default)
    {
        var basket = await _repo.GetAsync(userId, cancellationToken) ?? new Basket(userId);

        basket.AddItem(request.ProductId, request.Quantity);

        await _repo.SaveAsync(basket, cancellationToken);

        _logger.LogInformation(
            "Added product {ProductId} x{Quantity} to basket for {UserId}",
            request.ProductId,
            request.Quantity,
            userId);

        return ToDto(basket);
    }

    public async Task<BasketDto> RemoveItemAsync(Guid userId, Guid productId, CancellationToken cancellationToken = default)
    {
        var basket = await _repo.GetAsync(userId, cancellationToken) ?? new Basket(userId);

        basket.RemoveItem(productId);

        await _repo.SaveAsync(basket, cancellationToken);

        return ToDto(basket);
    }

    public Task ClearAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return _repo.DeleteAsync(userId, cancellationToken);
    }

    private static BasketDto ToDto(Basket basket)
    {
        return new BasketDto(
            basket.UserId,
            basket.Items.Select(i => new BasketItemDto(i.ProductId, i.Quantity)).ToList());
    }
}
