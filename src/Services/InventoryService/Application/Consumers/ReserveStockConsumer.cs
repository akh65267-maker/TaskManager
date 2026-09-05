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
            return;
        }

        try
        {
            item.Reserve(message.Quantity);
            await _repo.SaveChangesAsync(context.CancellationToken);

            _logger.LogInformation(
                "Reserved {Quantity} of product {ProductId} for order {OrderId}",
                message.Quantity,
                message.ProductId,
                message.OrderId);

            await context.Publish(new StockReserved(message.OrderId, message.ProductId));
        }
        catch (InsufficientStockException ex)
        {
            await context.Publish(new StockReservationFailed(message.OrderId, message.ProductId, ex.Message));
        }
    }
}
