using MediatR;

namespace CatalogService.Application.Products;

public sealed record GetProductByIdQuery(Guid Id) : IRequest<ProductDto?>;
