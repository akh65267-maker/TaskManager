using UserService.Domain;

namespace UserService.Application;

public interface IJwtTokenGenerator
{
    (string Token, DateTimeOffset ExpiresAtUtc) GenerateToken(User user);
}
