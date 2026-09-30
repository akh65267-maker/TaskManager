using Contracts.IntegrationEvents;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using OrderService.Application.Sagas;
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

        await TimeOutExpiredAsync(scope, db, cancellationToken);
        await PurgeExpiredTombstonesAsync(db, cancellationToken);
    }

    private async Task TimeOutExpiredAsync(IServiceScope scope, OrderDbContext db, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow - _options.Timeout;

        // Only sagas still waiting. A TimedOut row is kept on purpose and is
        // older than the deadline by definition, so without this filter every
        // tombstone would be "timed out" again on every tick, forever.
        var expired = await db.OrderSagaStates
            .AsNoTracking()
            .Where(s => s.CurrentState == OrderSagaStateMachine.AwaitingStockReservationName
                        && s.CreatedAtUtc < deadline)
            .Select(s => s.CorrelationId)
            .ToListAsync(cancellationToken);

        if (expired.Count == 0)
            return;

        // Published straight to the bus rather than through the outbox. The
        // sweep is already a retry loop - an undelivered timeout is republished
        // on the next tick because the saga is still awaiting - so outbox
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

    private async Task PurgeExpiredTombstonesAsync(OrderDbContext db, CancellationToken cancellationToken)
    {
        // Measured from CreatedAtUtc, which is when the saga started, not when it
        // timed out - so the window after the timeout is TimedOutRetention, give
        // or take up to one sweep interval. Deleted in the database directly:
        // nothing else is touching a row this old, and a message that does arrive
        // for it afterwards simply finds no saga, exactly as before this state existed.
        var purgeBefore = DateTimeOffset.UtcNow - _options.Timeout - _options.TimedOutRetention;

        var purged = await db.OrderSagaStates
            .Where(s => s.CurrentState == OrderSagaStateMachine.TimedOutName
                        && s.CreatedAtUtc < purgeBefore)
            .ExecuteDeleteAsync(cancellationToken);

        if (purged > 0)
        {
            _logger.LogInformation(
                "Removed {Count} timed-out checkout saga(s) past the {Retention} retention window",
                purged,
                _options.TimedOutRetention);
        }
    }
}
