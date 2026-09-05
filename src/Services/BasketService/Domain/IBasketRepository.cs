namespace BasketService.Domain;

public interface IBasketRepository
{
    Task<Basket?> GetAsync(Guid userId, CancellationToken cancellationToken = default);
    Task SaveAsync(Basket basket, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid userId, CancellationToken cancellationToken = default);
}
