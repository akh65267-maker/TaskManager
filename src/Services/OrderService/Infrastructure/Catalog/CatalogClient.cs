using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OrderService.Application.Catalog;
using OrderService.Application.Orders;

namespace OrderService.Infrastructure.Catalog;

/// <summary>
/// Reads prices from CatalogService's public <c>GET /products/{id}</c>, one request per
/// distinct product, in parallel. There is deliberately no retry: it would add latency to a
/// request a person is waiting on, and "try again shortly" is an honest answer.
/// </summary>
public sealed class CatalogClient : IProductPriceProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<CatalogClient> _logger;

    public CatalogClient(HttpClient http, ILogger<CatalogClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<Guid, decimal>> GetPricesAsync(
        IReadOnlyCollection<Guid> productIds,
        CancellationToken cancellationToken = default)
    {
        var lookups = productIds.Distinct().Select(id => GetPriceAsync(id, cancellationToken));
        var results = await Task.WhenAll(lookups);

        return results
            .Where(r => r.Price is not null)
            .ToDictionary(r => r.Id, r => r.Price!.Value);
    }

    private async Task<(Guid Id, decimal? Price)> GetPriceAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync($"products/{id}", cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return (id, null);

            response.EnsureSuccessStatusCode();

            var product = await response.Content.ReadFromJsonAsync<CatalogProduct>(cancellationToken)
                ?? throw new InvalidOperationException("CatalogService returned an empty product.");

            return (id, product.Price);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                   && ex is HttpRequestException or TaskCanceledException
                                       or InvalidOperationException or JsonException)
        {
            _logger.LogError(ex, "Could not read the price of product {ProductId} from CatalogService", id);
            throw OrderRejectedException.PricesUnavailable();
        }
    }

    private sealed record CatalogProduct(decimal Price);
}
