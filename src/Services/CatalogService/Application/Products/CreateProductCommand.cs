using MediatR;

namespace CatalogService.Application.Products;

public sealed record CreateProductCommand(string Name, string Description, decimal Price) : IRequest<Guid>;
