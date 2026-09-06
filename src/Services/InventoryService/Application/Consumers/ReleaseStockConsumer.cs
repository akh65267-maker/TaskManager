using Contracts.Commands;
using InventoryService.Domain;
using MassTransit;

namespace InventoryService.Application.Consumers;

public class ReleaseStockConsumer : IConsumer<ReleaseStock>
{
    private readonly IInventoryRepository _repo;
    private readonly ILogger<ReleaseStockConsumer> _logger;

    public ReleaseStockConsumer(IInventoryRepository repo, ILogger<ReleaseStockConsumer> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<ReleaseStock> context)
    {
        var message = context.Message;
        var item = await _repo.GetByProductIdAsync(message.ProductId, context.CancellationToken);

        if (item is null)
        {
            _logger.LogWarning(
                "Cannot release stock for unknown product {ProductId} (order {OrderId})",
                message.ProductId,
                message.OrderId);

            // Still commit so this message's inbox (dedup) entry is recorded.
            await _repo.SaveChangesAsync(context.CancellationToken);
            return;
        }

        item.Release(message.Quantity);
        await _repo.SaveChangesAsync(context.CancellationToken);

        _logger.LogInformation(
            "Released {Quantity} of product {ProductId} from order {OrderId}",
            message.Quantity,
            message.ProductId,
            message.OrderId);
    }
}
