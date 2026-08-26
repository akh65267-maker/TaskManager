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

    public async Task<IReadOnlyCollection<TaskDto>> GetAllAsync(CancellationToken cancellationToken  = default)
    {
        var tasks = await _repo.GetAllAsync(cancellationToken);

        return tasks.Select(task => new TaskDto(
            task.Id,
            task.Title,
            task.IsCompleted,
            task.OwnerId))
        .ToList();
    }

    public async Task<Guid> CreateAsync(CreateTaskRequest request, CancellationToken cancellationToken = default)
    {
        if (!await _knownUsers.ExistsAsync(request.OwnerId, cancellationToken))
            throw new ArgumentException($"No known user with id '{request.OwnerId}'.", nameof(request));

        var task = new TaskItem(request.Title, request.OwnerId);

        await _repo.AddAsync(task, cancellationToken);

        _logger.LogInformation(
            "Creating task with title {Title} for owner {OwnerId}",
            request.Title,
            request.OwnerId);


        return task.Id;
    }

    public async Task<TaskDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var task = await _repo.GetByIdAsync(id, cancellationToken);

        if (task is null)
            return null;

        return new TaskDto(task.Id, task.Title, task.IsCompleted, task.OwnerId);
    }

    public async Task<bool> CompleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var tasks = await _repo.GetByIdAsync(id, cancellationToken);

        if (tasks is null)
            return false;

        tasks.Complete();

        await _repo.SaveChangesAsync(cancellationToken);

        return true;
    }
}
