namespace OrderService.Domain;

public interface IOrderRepository
{
    Task<IReadOnlyCollection<Order>> GetAllByUserIdAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddAsync(Order order, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
