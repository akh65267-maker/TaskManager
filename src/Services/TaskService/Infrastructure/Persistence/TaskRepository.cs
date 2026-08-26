using Microsoft.EntityFrameworkCore;
using TaskService.Domain;

namespace TaskService.Infrastructure.Persistence;

public class TaskRepository : ITaskRepository
{
    private readonly TaskDbContext _db;

    public TaskRepository(TaskDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(TaskItem task, CancellationToken cancellationToken = default)
    {
        await _db.Tasks.AddAsync(task, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<TaskItem>> GetAllByOwnerAsync(Guid ownerId, CancellationToken cancellationToken = default)
    {
        return await _db.Tasks
            .AsNoTracking()
            .Where(x => x.OwnerId == ownerId)
            .ToListAsync(cancellationToken);
    }

    public async Task<TaskItem?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _db.Tasks
            .FirstOrDefaultAsync(x => x.Id == id);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return _db.SaveChangesAsync(cancellationToken);
    }
}
