using Contracts.IntegrationEvents;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using OrderService.Infrastructure.Persistence;

namespace OrderService.Infrastructure;

/// <summary>
/// Gives the checkout saga a deadline. Without one, a stock response that is
/// never delivered - dropped, or parked in an _error queue after exhausting
/// retries - leaves the saga in AwaitingStockReservation permanently: the order
/// stays Pending and any stock already reserved for it is never released.
///
/// The deadline is swept from the database rather than scheduled in the broker.
/// MassTransit's Schedule would need a message scheduler, and the two durable
/// options both cost infrastructure: RabbitMQ's delayed-exchange plugin is not
/// in the stock image, and Quartz brings its own schema. Sweeping needs neither
/// and is durable by construction, because the deadline is derived from
/// OrderSagaState.CreatedAtUtc, which is already persisted. A restart resumes
/// enforcing deadlines it never knew about; an in-memory scheduler would have
/// silently dropped them, which is the exact failure this exists to prevent.
///
/// The cost of that choice is granularity: a saga is cancelled up to one sweep
/// interval after its deadline rather than exactly on it.
/// </summary>
public sealed class CheckoutTimeoutSweeper : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CheckoutTimeoutOptions _options;
    private readonly ILogger<CheckoutTimeoutSweeper> _logger;

    public CheckoutTimeoutSweeper(
        IServiceScopeFactory scopeFactory,
        CheckoutTimeoutOptions options,
        ILogger<CheckoutTimeoutSweeper> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.SweepInterval);

        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed sweep must not take the service down. The deadline is
                // recomputed from persisted state every tick, so anything missed
                // here is simply picked up by the next one.
                _logger.LogWarning(ex, "Failed to sweep for timed-out checkouts");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();

        var deadline = DateTimeOffset.UtcNow - _options.Timeout;

        var expired = await db.OrderSagaStates
            .AsNoTracking()
            .Where(s => s.CreatedAtUtc < deadline)
            .Select(s => s.CorrelationId)
            .ToListAsync(cancellationToken);

        if (expired.Count == 0)
            return;

        // Published straight to the bus rather than through the outbox. The
        // sweep is already a retry loop - an undelivered timeout is republished
        // on the next tick because the saga row is still there - so outbox
        // durability would add nothing here beyond a write on every tick.
        var bus = scope.ServiceProvider.GetRequiredService<IBus>();

        foreach (var correlationId in expired)
        {
            _logger.LogWarning(
                "Checkout saga {CorrelationId} exceeded the {Timeout} stock-reservation timeout; cancelling and releasing reserved stock",
                correlationId,
                _options.Timeout);

            await bus.Publish(new CheckoutTimedOut(correlationId), cancellationToken);
        }
    }
}
