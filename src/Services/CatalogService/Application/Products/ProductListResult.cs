namespace CatalogService.Application.Products;

public sealed record ProductListResult(IReadOnlyCollection<ProductDto> Items, int TotalCount, int Page, int PageSize);
