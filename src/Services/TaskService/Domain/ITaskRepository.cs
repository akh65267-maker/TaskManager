namespace TaskService.Domain;

public interface ITaskRepository
{
    Task<IReadOnlyCollection<TaskItem>> GetAllByOwnerAsync(Guid ownerId, CancellationToken cancellationToken = default);
    Task<TaskItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddAsync(TaskItem task, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
