using System.Diagnostics.Metrics;

namespace OrderService.Application;

/// <summary>
/// The application-owned metrics for checkout. ASP.NET Core and MassTransit
/// already supply request/message rates and durations, so this covers only what
/// they cannot see: the state of the saga itself.
///
/// The gauges are observed from cached fields rather than queried on collect -
/// a Prometheus scrape must not run database queries on the collection thread.
/// <c>OrderMetricsCollector</c> refreshes them on a timer.
/// </summary>
public sealed class OrderMetrics
{
    public const string MeterName = "TaskManager.Orders";

    private readonly Counter<long> _finalized;
    private readonly Counter<long> _reservationFailures;

    private int _activeSagas;
    private double _oldestPendingOrderAgeSeconds;
    private int _outboxBacklog;

    public OrderMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _finalized = meter.CreateCounter<long>(
            "order_saga_finalized_total",
            description: "Sagas that reached a terminal outcome, by outcome.");

        _reservationFailures = meter.CreateCounter<long>(
            "order_stock_reservation_failed_total",
            description: "StockReservationFailed responses observed by the saga.");

        meter.CreateObservableGauge(
            "order_saga_active",
            () => Volatile.Read(ref _activeSagas),
            description: "Saga instances currently persisted, i.e. checkouts still in flight.");

        // The saga has no timeout (see docs/TODO.md), so a lost message leaves an
        // order Pending with its stock reserved forever. This age is the signal
        // that has happened: in normal operation it stays near zero, and it is
        // read from the orders table so it survives a service restart.
        meter.CreateObservableGauge(
            "order_pending_oldest_age_seconds",
            () => Volatile.Read(ref _oldestPendingOrderAgeSeconds),
            unit: "s",
            description: "Age of the oldest order still in Pending status; 0 when there is none.");

        meter.CreateObservableGauge(
            "order_outbox_backlog",
            () => Volatile.Read(ref _outboxBacklog),
            description: "Rows in the transactional outbox waiting to be delivered to RabbitMQ.");
    }

    public void RecordFinalized(bool hasFailure) =>
        _finalized.Add(1, new KeyValuePair<string, object?>("outcome", hasFailure ? "cancelled" : "confirmed"));

    /// <summary>
    /// Deliberately unlabelled. The failure reason reaching the saga is an
    /// exception message from InventoryService that embeds the requested and
    /// available quantities, so using it as a label would create a new
    /// Prometheus time series per failure. The reason stays in the logs and on
    /// the trace, where unbounded values are fine.
    /// </summary>
    public void RecordReservationFailure() => _reservationFailures.Add(1);

    public void UpdateGauges(int activeSagas, double oldestPendingOrderAgeSeconds, int outboxBacklog)
    {
        Volatile.Write(ref _activeSagas, activeSagas);
        Volatile.Write(ref _oldestPendingOrderAgeSeconds, oldestPendingOrderAgeSeconds);
        Volatile.Write(ref _outboxBacklog, outboxBacklog);
    }
}
