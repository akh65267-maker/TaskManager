using UserService.Domain;

namespace UserService.Application.Users;

public sealed record UserDto(Guid Id, string Email, string DisplayName, UserRole Role);
