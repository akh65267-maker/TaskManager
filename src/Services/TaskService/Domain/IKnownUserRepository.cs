namespace TaskService.Domain;

public interface IKnownUserRepository
{
    Task<bool> ExistsAsync(Guid userId, CancellationToken cancellationToken = default);
    Task UpsertAsync(Guid userId, string email, CancellationToken cancellationToken = default);
}
