using UserService.Domain;

namespace UserService.UnitTests.Domain;

public class UserTests
{
    [Theory]
    [InlineData(null, "Test", "hash")]
    [InlineData("", "Test", "hash")]
    [InlineData("test@example.com", null, "hash")]
    [InlineData("test@example.com", "", "hash")]
    [InlineData("test@example.com", "Test", null)]
    [InlineData("test@example.com", "Test", "")]
    public void Constructor_WithMissingFields_Throws(string? email, string? displayName, string? passwordHash)
    {
        Assert.Throws<ArgumentException>(() => new User(email!, displayName!, passwordHash!));
    }

    [Fact]
    public void Constructor_WithValidArguments_SetsProperties()
    {
        var user = new User("test@example.com", "Test User", "hash");

        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.Equal("test@example.com", user.Email);
        Assert.Equal("Test User", user.DisplayName);
        Assert.Equal("hash", user.PasswordHash);
        Assert.Equal(UserRole.Customer, user.Role);
    }

    [Fact]
    public void Constructor_WithExplicitRole_SetsRole()
    {
        var user = new User("admin@example.com", "Admin", "hash", UserRole.Admin);

        Assert.Equal(UserRole.Admin, user.Role);
    }
}
