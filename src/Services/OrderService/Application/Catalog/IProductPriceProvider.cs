namespace OrderService.Application.Catalog;

/// <summary>
/// The authoritative price of products, as CatalogService has them right now. An order's
/// prices are checked against this, never trusted from the caller.
/// </summary>
public interface IProductPriceProvider
{
    /// <summary>
    /// Prices for the products that exist; an id missing from the result is a product
    /// CatalogService does not know. Throws <see cref="Orders.OrderRejectedException"/> (503)
    /// when the catalog cannot be reached, because an unknown price must never be guessed.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, decimal>> GetPricesAsync(
        IReadOnlyCollection<Guid> productIds,
        CancellationToken cancellationToken = default);
}
