namespace UserService.Application.Users;

public sealed record LoginResult(string Token, DateTimeOffset ExpiresAtUtc);
