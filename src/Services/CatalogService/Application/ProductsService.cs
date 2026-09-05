using CatalogService.Application.Products;
using CatalogService.Domain;

namespace CatalogService.Application;

public class ProductsService
{
    private readonly IProductRepository _repo;
    private readonly ILogger<ProductsService> _logger;

    public ProductsService(IProductRepository repo, ILogger<ProductsService> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<ProductDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var products = await _repo.GetAllAsync(cancellationToken);

        return products.Select(p => new ProductDto(p.Id, p.Name, p.Description, p.Price)).ToList();
    }

    public async Task<ProductDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var product = await _repo.GetByIdAsync(id, cancellationToken);

        return product is null ? null : new ProductDto(product.Id, product.Name, product.Description, product.Price);
    }

    public async Task<Guid> CreateAsync(CreateProductRequest request, CancellationToken cancellationToken = default)
    {
        var product = new Product(request.Name, request.Description, request.Price);

        await _repo.AddAsync(product, cancellationToken);
        await _repo.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created product {ProductId} with name {Name}", product.Id, product.Name);

        return product.Id;
    }
}
