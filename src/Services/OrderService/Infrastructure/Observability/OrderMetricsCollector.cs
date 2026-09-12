using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using OrderService.Application;
using OrderService.Domain;
using OrderService.Infrastructure.Persistence;

namespace OrderService.Infrastructure.Observability;

/// <summary>
/// Refreshes <see cref="OrderMetrics"/>'s gauges from the database on a timer.
/// The gauges describe persisted state (in-flight sagas, stuck orders, outbox
/// backlog), so they are read from the database rather than counted in memory:
/// an in-process counter would reset on restart and would miss rows written by
/// another instance.
/// </summary>
public sealed class OrderMetricsCollector : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OrderMetrics _metrics;
    private readonly ILogger<OrderMetricsCollector> _logger;

    public OrderMetricsCollector(
        IServiceScopeFactory scopeFactory,
        OrderMetrics metrics,
        ILogger<OrderMetricsCollector> logger)
    {
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                await CollectAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed collection must not take the service down - the
                // gauges simply keep their previous values until the next tick.
                _logger.LogWarning(ex, "Failed to refresh order metrics");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CollectAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();

        var activeSagas = await db.OrderSagaStates.CountAsync(cancellationToken);

        var oldestPendingCreatedAt = await db.Orders
            .Where(o => o.Status == OrderStatus.Pending)
            .MinAsync(o => (DateTimeOffset?)o.CreatedAtUtc, cancellationToken);

        var oldestPendingAge = oldestPendingCreatedAt is null
            ? 0
            : Math.Max(0, (DateTimeOffset.UtcNow - oldestPendingCreatedAt.Value).TotalSeconds);

        var outboxBacklog = await db.Set<OutboxMessage>().CountAsync(cancellationToken);

        _metrics.UpdateGauges(activeSagas, oldestPendingAge, outboxBacklog);
    }
}
