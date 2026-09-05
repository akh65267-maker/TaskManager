namespace CatalogService.Application.Products;

public sealed record CreateProductRequest(string Name, string Description, decimal Price);
