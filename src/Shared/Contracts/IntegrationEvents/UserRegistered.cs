namespace Contracts.IntegrationEvents;

public sealed record UserRegistered(Guid UserId, string Email, DateTimeOffset RegisteredAtUtc);
