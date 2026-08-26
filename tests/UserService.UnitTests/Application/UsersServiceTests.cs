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
    private readonly Mock<IPublishEndpoint> _publishEndpoint = new();
    private readonly UsersService _sut;

    public UsersServiceTests()
    {
        _sut = new UsersService(_userRepo.Object, _publishEndpoint.Object, Mock.Of<ILogger<UsersService>>());
    }

    [Fact]
    public async Task RegisterAsync_WithDuplicateEmail_ThrowsAndDoesNotPublish()
    {
        _userRepo.Setup(x => x.EmailExistsAsync("dup@example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.RegisterAsync(new RegisterUserRequest("dup@example.com", "Someone")));

        _userRepo.Verify(x => x.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
        _publishEndpoint.Verify(x => x.Publish(It.IsAny<UserRegistered>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RegisterAsync_WithMissingEmail_ThrowsWithoutCheckingRepo()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.RegisterAsync(new RegisterUserRequest("", "Someone")));

        _userRepo.Verify(x => x.EmailExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RegisterAsync_WithNewEmail_SavesAndPublishesUserRegistered()
    {
        _userRepo.Setup(x => x.EmailExistsAsync("new@example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var id = await _sut.RegisterAsync(new RegisterUserRequest("new@example.com", "New Person"));

        Assert.NotEqual(Guid.Empty, id);

        _userRepo.Verify(x => x.AddAsync(
            It.Is<User>(u => u.Email == "new@example.com" && u.Id == id),
            It.IsAny<CancellationToken>()), Times.Once);

        _publishEndpoint.Verify(x => x.Publish(
            It.Is<UserRegistered>(e => e.Email == "new@example.com" && e.UserId == id),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
