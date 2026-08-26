using UserService.Infrastructure.Security;

namespace UserService.UnitTests.Infrastructure;

public class Pbkdf2PasswordHasherTests
{
    private readonly Pbkdf2PasswordHasher _sut = new();

    [Fact]
    public void Verify_WithCorrectPassword_ReturnsTrue()
    {
        var hash = _sut.Hash("correct-password");

        Assert.True(_sut.Verify("correct-password", hash));
    }

    [Fact]
    public void Verify_WithWrongPassword_ReturnsFalse()
    {
        var hash = _sut.Hash("correct-password");

        Assert.False(_sut.Verify("wrong-password", hash));
    }

    [Fact]
    public void Verify_WithMalformedHash_ReturnsFalseInsteadOfThrowing()
    {
        Assert.False(_sut.Verify("any-password", "not-a-valid-hash"));
    }

    [Fact]
    public void Hash_ProducesDifferentOutputForSamePassword()
    {
        var hash1 = _sut.Hash("same-password");
        var hash2 = _sut.Hash("same-password");

        Assert.NotEqual(hash1, hash2);
    }
}
