using UserService.Domain;

namespace UserService.UnitTests.Domain;

public class UserTests
{
    [Theory]
    [InlineData(null, "Test")]
    [InlineData("", "Test")]
    [InlineData("test@example.com", null)]
    [InlineData("test@example.com", "")]
    public void Constructor_WithMissingFields_Throws(string? email, string? displayName)
    {
        Assert.Throws<ArgumentException>(() => new User(email!, displayName!));
    }

    [Fact]
    public void Constructor_WithValidArguments_SetsProperties()
    {
        var user = new User("test@example.com", "Test User");

        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.Equal("test@example.com", user.Email);
        Assert.Equal("Test User", user.DisplayName);
    }
}
