using Contracts.IntegrationEvents;
using MassTransit;
using Microsoft.Extensions.Logging;
using Moq;
using UserService.Application;
using UserService.Application.Users;
using UserService.Domain;

namespace UserService.UnitTests.Application;

public class UsersServiceTests
{
    private readonly Mock<IUserRepository> _userRepo = new();
    private readonly Mock<IPasswordHasher> _passwordHasher = new();
    private readonly Mock<IJwtTokenGenerator> _tokenGenerator = new();
    private readonly Mock<IPublishEndpoint> _publishEndpoint = new();
    private readonly UsersService _sut;

    public UsersServiceTests()
    {
        _sut = new UsersService(
            _userRepo.Object,
            _passwordHasher.Object,
            _tokenGenerator.Object,
            _publishEndpoint.Object,
            Mock.Of<ILogger<UsersService>>());
    }

    [Fact]
    public async Task RegisterAsync_WithDuplicateEmail_ThrowsAndDoesNotPublish()
    {
        _passwordHasher.Setup(x => x.Hash(It.IsAny<string>())).Returns("hashed");
        _userRepo.Setup(x => x.EmailExistsAsync("dup@example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.RegisterAsync(new RegisterUserRequest("dup@example.com", "Someone", "password123")));

        _userRepo.Verify(x => x.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
        _publishEndpoint.Verify(x => x.Publish(It.IsAny<UserRegistered>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RegisterAsync_WithMissingEmail_ThrowsWithoutCheckingRepo()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.RegisterAsync(new RegisterUserRequest("", "Someone", "password123")));

        _userRepo.Verify(x => x.EmailExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RegisterAsync_WithShortPassword_ThrowsWithoutCheckingRepo()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.RegisterAsync(new RegisterUserRequest("new@example.com", "Someone", "short")));

        _userRepo.Verify(x => x.EmailExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RegisterAsync_WithNewEmail_SavesHashedPasswordAndPublishesUserRegistered()
    {
        _passwordHasher.Setup(x => x.Hash("password123")).Returns("hashed-password");
        _userRepo.Setup(x => x.EmailExistsAsync("new@example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var id = await _sut.RegisterAsync(new RegisterUserRequest("new@example.com", "New Person", "password123"));

        Assert.NotEqual(Guid.Empty, id);

        _userRepo.Verify(x => x.AddAsync(
            It.Is<User>(u => u.Email == "new@example.com" && u.Id == id && u.PasswordHash == "hashed-password"),
            It.IsAny<CancellationToken>()), Times.Once);

        _publishEndpoint.Verify(x => x.Publish(
            It.Is<UserRegistered>(e => e.Email == "new@example.com" && e.UserId == id),
            It.IsAny<CancellationToken>()), Times.Once);

        _userRepo.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RegisterAsync_PublishesBeforeSavingChanges()
    {
        // With the transactional outbox, Publish must be buffered before the
        // single SaveChangesAsync call that commits it — publishing after
        // SaveChangesAsync would mean the message never gets flushed.
        var sequence = new MockSequence();
        _passwordHasher.Setup(x => x.Hash(It.IsAny<string>())).Returns("hashed");
        _userRepo.Setup(x => x.EmailExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _userRepo.InSequence(sequence).Setup(x => x.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _publishEndpoint.InSequence(sequence).Setup(x => x.Publish(It.IsAny<UserRegistered>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _userRepo.InSequence(sequence).Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _sut.RegisterAsync(new RegisterUserRequest("new@example.com", "New Person", "password123"));
    }

    [Fact]
    public async Task LoginAsync_WithUnknownEmail_ThrowsInvalidCredentials()
    {
        _userRepo.Setup(x => x.GetByEmailAsync("missing@example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        await Assert.ThrowsAsync<InvalidCredentialsException>(
            () => _sut.LoginAsync(new LoginRequest("missing@example.com", "password123")));
    }

    [Fact]
    public async Task LoginAsync_WithWrongPassword_ThrowsInvalidCredentials()
    {
        var user = new User("test@example.com", "Test", "stored-hash");
        _userRepo.Setup(x => x.GetByEmailAsync("test@example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _passwordHasher.Setup(x => x.Verify("wrong-password", "stored-hash")).Returns(false);

        await Assert.ThrowsAsync<InvalidCredentialsException>(
            () => _sut.LoginAsync(new LoginRequest("test@example.com", "wrong-password")));
    }

    [Fact]
    public async Task LoginAsync_WithCorrectCredentials_ReturnsGeneratedToken()
    {
        var user = new User("test@example.com", "Test", "stored-hash");
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        _userRepo.Setup(x => x.GetByEmailAsync("test@example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _passwordHasher.Setup(x => x.Verify("correct-password", "stored-hash")).Returns(true);
        _tokenGenerator.Setup(x => x.GenerateToken(user)).Returns(("jwt-token", expiresAt));

        var result = await _sut.LoginAsync(new LoginRequest("test@example.com", "correct-password"));

        Assert.Equal("jwt-token", result.Token);
        Assert.Equal(expiresAt, result.ExpiresAtUtc);
    }
}
