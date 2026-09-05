using CatalogService.Application;
using CatalogService.Application.Products;
using CatalogService.Domain;
using Microsoft.Extensions.Logging;
using Moq;

namespace CatalogService.UnitTests.Application;

public class ProductsServiceTests
{
    private readonly Mock<IProductRepository> _repo = new();
    private readonly ProductsService _sut;

    public ProductsServiceTests()
    {
        _sut = new ProductsService(_repo.Object, Mock.Of<ILogger<ProductsService>>());
    }

    [Fact]
    public async Task CreateAsync_WithInvalidName_ThrowsWithoutAdding()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.CreateAsync(new CreateProductRequest("", "desc", 9.99m)));

        _repo.Verify(x => x.AddAsync(It.IsAny<Product>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_WithValidRequest_AddsAndSavesAndReturnsId()
    {
        var id = await _sut.CreateAsync(new CreateProductRequest("Widget", "desc", 9.99m));

        Assert.NotEqual(Guid.Empty, id);
        _repo.Verify(x => x.AddAsync(
            It.Is<Product>(p => p.Name == "Widget" && p.Price == 9.99m),
            It.IsAny<CancellationToken>()), Times.Once);
        _repo.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetByIdAsync_NotFound_ReturnsNull()
    {
        _repo.Setup(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Product?)null);

        var result = await _sut.GetByIdAsync(Guid.NewGuid());

        Assert.Null(result);
    }
}
