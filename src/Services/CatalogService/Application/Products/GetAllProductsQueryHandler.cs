using CatalogService.Domain;
using MediatR;

namespace CatalogService.Application.Products;

public sealed class GetAllProductsQueryHandler : IRequestHandler<GetAllProductsQuery, IReadOnlyCollection<ProductDto>>
{
    private readonly IProductRepository _repo;

    public GetAllProductsQueryHandler(IProductRepository repo)
    {
        _repo = repo;
    }

    public async Task<IReadOnlyCollection<ProductDto>> Handle(GetAllProductsQuery request, CancellationToken cancellationToken)
    {
        var products = await _repo.GetAllAsync(cancellationToken);

        return products.Select(p => new ProductDto(p.Id, p.Name, p.Description, p.Price)).ToList();
    }
}
