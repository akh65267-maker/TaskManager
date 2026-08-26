namespace UserService.Application.Users;

public sealed record RegisterUserRequest(string Email, string DisplayName, string Password);
