using Contracts.Commands;
using Contracts.IntegrationEvents;
using InventoryService.Domain;
using MassTransit;

namespace InventoryService.Application.Consumers;

public class ReserveStockConsumer : IConsumer<ReserveStock>
{
    private readonly IInventoryRepository _repo;
    private readonly ILogger<ReserveStockConsumer> _logger;

    public ReserveStockConsumer(IInventoryRepository repo, ILogger<ReserveStockConsumer> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<ReserveStock> context)
    {
        var message = context.Message;
        var item = await _repo.GetByProductIdAsync(message.ProductId, context.CancellationToken);

        if (item is null)
        {
            await context.Publish(new StockReservationFailed(message.OrderId, message.ProductId, "No inventory record for this product."));

            // Even with nothing to reserve, SaveChangesAsync must still run once:
            // it's what flushes the buffered publish above into the outbox table
            // and records this message's inbox (dedup) entry. Skipping it here
            // would mean a redelivery of this exact message reprocesses from
            // scratch instead of being recognized as already handled.
            await _repo.SaveChangesAsync(context.CancellationToken);
            return;
        }

        try
        {
            item.Reserve(message.Quantity);

            // Publish before SaveChangesAsync, not after: the outbox only
            // flushes a buffered publish on the next SaveChanges call on this
            // same DbContext. Publishing afterward would leave the message
            // buffered with no later SaveChanges call to pick it up.
            await context.Publish(new StockReserved(message.OrderId, message.ProductId));

            await _repo.SaveChangesAsync(context.CancellationToken);

            _logger.LogInformation(
                "Reserved {Quantity} of product {ProductId} for order {OrderId}",
                message.Quantity,
                message.ProductId,
                message.OrderId);
        }
        catch (InsufficientStockException ex)
        {
            await context.Publish(new StockReservationFailed(message.OrderId, message.ProductId, ex.Message));
            await _repo.SaveChangesAsync(context.CancellationToken);
        }
    }
}
