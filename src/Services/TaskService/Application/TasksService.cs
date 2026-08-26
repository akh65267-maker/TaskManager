using TaskService.Application.Tasks;
using TaskService.Domain;

namespace TaskService.Application;

public class TasksService
{
    private readonly ITaskRepository _repo;
    private readonly IKnownUserRepository _knownUsers;
    private readonly ILogger<TasksService> _logger;

    public TasksService(ITaskRepository repo, IKnownUserRepository knownUsers, ILogger<TasksService> logger)
    {
        _repo = repo;
        _knownUsers = knownUsers;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<TaskDto>> GetAllAsync(Guid ownerId, CancellationToken cancellationToken = default)
    {
        var tasks = await _repo.GetAllByOwnerAsync(ownerId, cancellationToken);

        return tasks.Select(task => new TaskDto(
            task.Id,
            task.Title,
            task.IsCompleted,
            task.OwnerId))
        .ToList();
    }

    public async Task<Guid> CreateAsync(CreateTaskRequest request, Guid ownerId, CancellationToken cancellationToken = default)
    {
        var task = new TaskItem(request.Title, ownerId);

        if (!await _knownUsers.ExistsAsync(task.OwnerId, cancellationToken))
            throw new ArgumentException($"No known user with id '{task.OwnerId}'.", nameof(ownerId));

        await _repo.AddAsync(task, cancellationToken);

        _logger.LogInformation(
            "Creating task with title {Title} for owner {OwnerId}",
            request.Title,
            ownerId);


        return task.Id;
    }

    public async Task<TaskDto?> GetByIdAsync(Guid id, Guid ownerId, CancellationToken cancellationToken)
    {
        var task = await _repo.GetByIdAsync(id, cancellationToken);

        if (task is null || task.OwnerId != ownerId)
            return null;

        return new TaskDto(task.Id, task.Title, task.IsCompleted, task.OwnerId);
    }

    public async Task<bool> CompleteAsync(Guid id, Guid ownerId, CancellationToken cancellationToken = default)
    {
        var task = await _repo.GetByIdAsync(id, cancellationToken);

        if (task is null || task.OwnerId != ownerId)
            return false;

        task.Complete();

        await _repo.SaveChangesAsync(cancellationToken);

        return true;
    }
}
