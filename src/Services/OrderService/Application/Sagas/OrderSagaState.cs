using MassTransit;

namespace OrderService.Application.Sagas;

/// <summary>
/// Saga instance tracking one order's stock-reservation checkout. Persisted
/// via MassTransit's EF Core saga repository in OrderService's own database
/// (OrderService is the natural coordinator of its own checkout process).
/// ItemsJson/ReservedProductIdsJson are JSON blobs rather than owned EF
/// collections to keep the saga entity mapping simple.
/// </summary>
public class OrderSagaState : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = default!;
    public byte[]? RowVersion { get; set; }

    /// <summary>
    /// When the saga started. Read by <c>CheckoutTimeoutSweeper</c> to find
    /// checkouts that have been awaiting stock responses for too long; it lives
    /// on the saga rather than being derived from the order so the sweep is a
    /// single-table query against the rows it is actually scanning.
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid UserId { get; set; }
    public string ItemsJson { get; set; } = "[]";
    public string ReservedProductIdsJson { get; set; } = "[]";
    public int TotalItems { get; set; }
    public int ResponseCount { get; set; }
    public bool HasFailure { get; set; }
    public string? FailureReason { get; set; }
}
