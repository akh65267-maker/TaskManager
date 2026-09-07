using Contracts.IntegrationEvents;
using MassTransit;
using UserService.Application.Users;
using UserService.Domain;

namespace UserService.Application;

public class UsersService
{
    private readonly IUserRepository _repo;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenGenerator _tokenGenerator;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<UsersService> _logger;

    public UsersService(
        IUserRepository repo,
        IPasswordHasher passwordHasher,
        IJwtTokenGenerator tokenGenerator,
        IPublishEndpoint publishEndpoint,
        ILogger<UsersService> logger)
    {
        _repo = repo;
        _passwordHasher = passwordHasher;
        _tokenGenerator = tokenGenerator;
        _publishEndpoint = publishEndpoint;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<UserDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var users = await _repo.GetAllAsync(cancellationToken);

        return users.Select(user => new UserDto(user.Id, user.Email, user.DisplayName, user.Role)).ToList();
    }

    public async Task<UserDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await _repo.GetByIdAsync(id, cancellationToken);

        return user is null ? null : new UserDto(user.Id, user.Email, user.DisplayName, user.Role);
    }

    public async Task<Guid> RegisterAsync(RegisterUserRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
            throw new ArgumentException("Password must be at least 8 characters.", nameof(request));

        var passwordHash = _passwordHasher.Hash(request.Password);
        var user = new User(request.Email, request.DisplayName, passwordHash);

        if (await _repo.EmailExistsAsync(user.Email, cancellationToken))
            throw new ArgumentException($"A user with email '{user.Email}' already exists.", nameof(request));

        await _repo.AddAsync(user, cancellationToken);

        await _publishEndpoint.Publish(
            new UserRegistered(user.Id, user.Email, DateTimeOffset.UtcNow),
            cancellationToken);

        // Single SaveChangesAsync commits both the user row and the buffered
        // outbox message in one DB transaction (MassTransit's UseBusOutbox
        // intercepts SaveChanges to flush the outbox atomically with it).
        // Publish must be called before this, not after: if Publish is called
        // after SaveChangesAsync already committed, the outbox never gets a
        // SaveChanges call to flush into, and the message is silently lost.
        await _repo.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Registered user {UserId} with email {Email}", user.Id, user.Email);

        return user.Id;
    }

    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var user = await _repo.GetByEmailAsync(request.Email, cancellationToken);

        if (user is null || !_passwordHasher.Verify(request.Password, user.PasswordHash))
            throw new InvalidCredentialsException();

        var (token, expiresAtUtc) = _tokenGenerator.GenerateToken(user);

        return new LoginResult(token, expiresAtUtc);
    }
}
