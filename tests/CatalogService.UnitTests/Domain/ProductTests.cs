using CatalogService.Domain;

namespace CatalogService.UnitTests.Domain;

public class ProductTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithInvalidName_Throws(string? name)
    {
        Assert.Throws<ArgumentException>(() => new Product(name!, "A description", 9.99m, "Electronics"));
    }

    [Fact]
    public void Constructor_WithNegativePrice_Throws()
    {
        Assert.Throws<ArgumentException>(() => new Product("Widget", "A description", -1m, "Electronics"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithInvalidCategory_Throws(string? category)
    {
        Assert.Throws<ArgumentException>(() => new Product("Widget", "A description", 9.99m, category!));
    }

    [Fact]
    public void Constructor_WithNullDescription_DefaultsToEmptyString()
    {
        var product = new Product("Widget", null!, 9.99m, "Electronics");

        Assert.Equal(string.Empty, product.Description);
    }

    [Fact]
    public void Constructor_WithValidArguments_SetsProperties()
    {
        var product = new Product("Widget", "A description", 9.99m, "Electronics");

        Assert.NotEqual(Guid.Empty, product.Id);
        Assert.Equal("Widget", product.Name);
        Assert.Equal("A description", product.Description);
        Assert.Equal(9.99m, product.Price);
        Assert.Equal("Electronics", product.Category);
    }
}
