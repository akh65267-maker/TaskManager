using Microsoft.EntityFrameworkCore;
using TaskService.Domain;

namespace TaskService.Infrastructure.Persistence;

public class KnownUserRepository : IKnownUserRepository
{
    private readonly TaskDbContext _db;

    public KnownUserRepository(TaskDbContext db)
    {
        _db = db;
    }

    public async Task<bool> ExistsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await _db.KnownUsers
            .AsNoTracking()
            .AnyAsync(x => x.UserId == userId, cancellationToken);
    }

    public async Task UpsertAsync(Guid userId, string email, CancellationToken cancellationToken = default)
    {
        if (await ExistsAsync(userId, cancellationToken))
            return;

        await _db.KnownUsers.AddAsync(new KnownUser(userId, email), cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
