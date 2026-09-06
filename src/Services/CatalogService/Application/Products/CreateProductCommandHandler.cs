using CatalogService.Domain;
using MediatR;

namespace CatalogService.Application.Products;

public sealed class CreateProductCommandHandler : IRequestHandler<CreateProductCommand, Guid>
{
    private readonly IProductRepository _repo;
    private readonly ILogger<CreateProductCommandHandler> _logger;

    public CreateProductCommandHandler(IProductRepository repo, ILogger<CreateProductCommandHandler> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task<Guid> Handle(CreateProductCommand request, CancellationToken cancellationToken)
    {
        var product = new Product(request.Name, request.Description, request.Price);

        await _repo.AddAsync(product, cancellationToken);
        await _repo.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created product {ProductId} with name {Name}", product.Id, product.Name);

        return product.Id;
    }
}
