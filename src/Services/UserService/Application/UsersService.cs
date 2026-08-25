using Contracts.IntegrationEvents;
using MassTransit;
using UserService.Application.Users;
using UserService.Domain;

namespace UserService.Application;

public class UsersService
{
    private readonly IUserRepository _repo;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<UsersService> _logger;

    public UsersService(IUserRepository repo, IPublishEndpoint publishEndpoint, ILogger<UsersService> logger)
    {
        _repo = repo;
        _publishEndpoint = publishEndpoint;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<UserDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var users = await _repo.GetAllAsync(cancellationToken);

        return users.Select(user => new UserDto(user.Id, user.Email, user.DisplayName)).ToList();
    }

    public async Task<UserDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await _repo.GetByIdAsync(id, cancellationToken);

        return user is null ? null : new UserDto(user.Id, user.Email, user.DisplayName);
    }

    public async Task<Guid> RegisterAsync(RegisterUserRequest request, CancellationToken cancellationToken = default)
    {
        var user = new User(request.Email, request.DisplayName);

        if (await _repo.EmailExistsAsync(user.Email, cancellationToken))
            throw new ArgumentException($"A user with email '{user.Email}' already exists.", nameof(request));

        await _repo.AddAsync(user, cancellationToken);

        _logger.LogInformation("Registered user {UserId} with email {Email}", user.Id, user.Email);

        await _publishEndpoint.Publish(
            new UserRegistered(user.Id, user.Email, DateTimeOffset.UtcNow),
            cancellationToken);

        return user.Id;
    }
}
