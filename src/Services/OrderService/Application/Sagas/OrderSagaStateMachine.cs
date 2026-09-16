using System.Text.Json;
using Contracts.Commands;
using Contracts.Common;
using Contracts.IntegrationEvents;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Domain;

namespace OrderService.Application.Sagas;

/// <summary>
/// Orchestrates checkout: on OrderSubmitted, sends one ReserveStock command
/// per line item, then waits for a StockReserved/StockReservationFailed
/// response for each. Once every item has responded, either confirms the
/// order (all reserved) or compensates by releasing whatever *did* succeed
/// and cancels the order (any failure).
/// </summary>
public class OrderSagaStateMachine : MassTransitStateMachine<OrderSagaState>
{
    public State AwaitingStockReservation { get; private set; } = default!;

    public Event<OrderSubmitted> OrderSubmittedEvent { get; private set; } = default!;
    public Event<StockReserved> StockReservedEvent { get; private set; } = default!;
    public Event<StockReservationFailed> StockReservationFailedEvent { get; private set; } = default!;

    public OrderSagaStateMachine()
    {
        InstanceState(x => x.CurrentState);

        Event(() => OrderSubmittedEvent, e =>
        {
            e.CorrelateById(context => context.Message.OrderId);
            e.SelectId(context => context.Message.OrderId);
        });
        //Event(() => OrderSubmittedEvent, x => x.CorrelateById(context => context.Message.OrderId));
        Event(() => StockReservedEvent, x => x.CorrelateById(context => context.Message.OrderId));
        Event(() => StockReservationFailedEvent, x => x.CorrelateById(context => context.Message.OrderId));

        Initially(
            When(OrderSubmittedEvent)
                .Then(context =>
                {
                    context.Saga.UserId = context.Message.UserId;
                    context.Saga.TotalItems = context.Message.Items.Count;
                    context.Saga.ResponseCount = 0;
                    context.Saga.HasFailure = false;
                    context.Saga.ItemsJson = JsonSerializer.Serialize(context.Message.Items);
                    context.Saga.ReservedProductIdsJson = "[]";
                })
                .ThenAsync(async context =>
                {
                    foreach (var item in context.Message.Items)
                    {
                        await context.Publish(new ReserveStock(context.Message.OrderId, item.ProductId, item.Quantity));
                    }
                })
                .TransitionTo(AwaitingStockReservation)
        );

        During(AwaitingStockReservation,
            When(StockReservedEvent)
                .Then(context =>
                {
                    context.Saga.ResponseCount++;

                    var reserved = JsonSerializer.Deserialize<List<Guid>>(context.Saga.ReservedProductIdsJson) ?? new List<Guid>();
                    reserved.Add(context.Message.ProductId);
                    context.Saga.ReservedProductIdsJson = JsonSerializer.Serialize(reserved);
                })
                .IfElse(context => context.Saga.ResponseCount >= context.Saga.TotalItems,
                    complete => complete.ThenAsync(FinalizeAsync).Finalize(),
                    incomplete => incomplete),

            When(StockReservationFailedEvent)
                .Then(context => context.GetPayload<IServiceProvider>()
                    .GetRequiredService<OrderMetrics>()
                    .RecordReservationFailure())
                .Then(context => context.Saga.HasFailure = true)
                .Then(context => context.Saga.FailureReason ??= context.Message.Reason)
                .Then(context => context.Saga.ResponseCount++)
                .IfElse(context => context.Saga.ResponseCount >= context.Saga.TotalItems,
                    complete => complete.ThenAsync(FinalizeAsync).Finalize(),
                    incomplete => incomplete)
        );

        SetCompletedWhenFinalized();
    }

    private static async Task FinalizeAsync<T>(BehaviorContext<OrderSagaState, T> context) where T : class
    {
        var saga = context.Saga;
        var provider = context.GetPayload<IServiceProvider>();
        var repo = provider.GetRequiredService<IOrderRepository>();

        var order = await repo.GetByIdAsync(saga.CorrelationId);
        if (order is null)
            return;

        if (saga.HasFailure)
        {
            var items = JsonSerializer.Deserialize<List<OrderLineItem>>(saga.ItemsJson) ?? new List<OrderLineItem>();
            var reservedProductIds = JsonSerializer.Deserialize<List<Guid>>(saga.ReservedProductIdsJson) ?? new List<Guid>();

            foreach (var productId in reservedProductIds)
            {
                var item = items.FirstOrDefault(i => i.ProductId == productId);
                if (item is not null)
                    await context.Publish(new ReleaseStock(saga.CorrelationId, productId, item.Quantity));
            }

            order.Cancel(saga.FailureReason ?? "Unable to reserve stock for one or more items.");
        }
        else
        {
            order.Confirm();
        }

        await repo.SaveChangesAsync();

        provider.GetRequiredService<OrderMetrics>().RecordFinalized(saga.HasFailure);
    }
}
