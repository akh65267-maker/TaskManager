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

    public Guid UserId { get; set; }
    public string ItemsJson { get; set; } = "[]";
    public string ReservedProductIdsJson { get; set; } = "[]";
    public int TotalItems { get; set; }
    public int ResponseCount { get; set; }
    public bool HasFailure { get; set; }
}
