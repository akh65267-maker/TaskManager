using MediatR;

namespace CatalogService.Application.Products;

public sealed record GetAllProductsQuery : IRequest<IReadOnlyCollection<ProductDto>>;
