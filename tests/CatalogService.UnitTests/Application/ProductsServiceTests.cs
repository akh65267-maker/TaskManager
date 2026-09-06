using CatalogService.Application.Products;
using CatalogService.Domain;
using Microsoft.Extensions.Logging;
using Moq;

namespace CatalogService.UnitTests.Application;

public class ProductsServiceTests
{
    private readonly Mock<IProductRepository> _repo = new();

    [Fact]
    public async Task CreateProductCommand_WithInvalidName_ThrowsWithoutAdding()
    {
        var sut = new CreateProductCommandHandler(_repo.Object, Mock.Of<ILogger<CreateProductCommandHandler>>());

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.Handle(new CreateProductCommand("", "desc", 9.99m, "Electronics"), CancellationToken.None));

        _repo.Verify(x => x.AddAsync(It.IsAny<Product>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateProductCommand_WithValidRequest_AddsAndSavesAndReturnsId()
    {
        var sut = new CreateProductCommandHandler(_repo.Object, Mock.Of<ILogger<CreateProductCommandHandler>>());

        var id = await sut.Handle(new CreateProductCommand("Widget", "desc", 9.99m, "Electronics"), CancellationToken.None);

        Assert.NotEqual(Guid.Empty, id);
        _repo.Verify(x => x.AddAsync(
            It.Is<Product>(p => p.Name == "Widget" && p.Price == 9.99m && p.Category == "Electronics"),
            It.IsAny<CancellationToken>()), Times.Once);
        _repo.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetProductByIdQuery_NotFound_ReturnsNull()
    {
        _repo.Setup(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Product?)null);

        var sut = new GetProductByIdQueryHandler(_repo.Object);

        var result = await sut.Handle(new GetProductByIdQuery(Guid.NewGuid()), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ProductListQuery_ReturnsPagedResultFromRepository()
    {
        var product = new Product("Widget", "desc", 9.99m, "Electronics");
        _repo.Setup(x => x.SearchAsync(
                "Electronics", null, null, null, ProductSortBy.Newest, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new[] { product }, 1));

        var sut = new ProductListQueryHandler(_repo.Object);

        var result = await sut.Handle(
            new ProductListQuery("Electronics", null, null, null, ProductSortBy.Newest, 1, 20),
            CancellationToken.None);

        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
        Assert.Equal("Widget", result.Items.First().Name);
    }
}
