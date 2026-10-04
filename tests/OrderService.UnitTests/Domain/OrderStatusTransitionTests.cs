using OrderService.Domain;

namespace OrderService.UnitTests.Domain;

public class OrderStatusTransitionTests
{
    private static Order NewPendingOrder() =>
        new(Guid.NewGuid(), new List<OrderItem> { new(Guid.NewGuid(), 1, 9.99m) });

    [Fact]
    public void NewOrder_StartsAsPending()
    {
        var order = NewPendingOrder();

        Assert.Equal(OrderStatus.Pending, order.Status);
    }

    [Fact]
    public void Confirm_FromPending_TransitionsToConfirmed()
    {
        var order = NewPendingOrder();

        order.Confirm();

        Assert.Equal(OrderStatus.Confirmed, order.Status);
    }

    [Fact]
    public void Cancel_FromPending_TransitionsToCancelledWithReason()
    {
        var order = NewPendingOrder();

        order.Cancel("Out of stock.");

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal("Out of stock.", order.CancellationReason);
    }

    [Fact]
    public void Confirm_ReportsThatItChangedTheOrder()
    {
        Assert.True(NewPendingOrder().Confirm());
    }

    [Fact]
    public void Cancel_ReportsThatItChangedTheOrder()
    {
        Assert.True(NewPendingOrder().Cancel("Out of stock."));
    }

    [Fact]
    public void Confirm_AlreadyConfirmed_IsANoOp()
    {
        var order = NewPendingOrder();
        order.Confirm();

        var changed = order.Confirm();

        Assert.False(changed);
        Assert.Equal(OrderStatus.Confirmed, order.Status);
    }

    [Fact]
    public void Cancel_AlreadyCancelled_IsANoOpAndKeepsTheOriginalReason()
    {
        var order = NewPendingOrder();
        order.Cancel("Out of stock.");

        var changed = order.Cancel("Timed out.");

        Assert.False(changed);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal("Out of stock.", order.CancellationReason);
    }

    [Fact]
    public void Cancel_AlreadyConfirmed_StillThrows()
    {
        var order = NewPendingOrder();
        order.Confirm();

        Assert.Throws<InvalidOperationException>(() => order.Cancel("Out of stock."));
        Assert.Equal(OrderStatus.Confirmed, order.Status);
    }

    [Fact]
    public void Confirm_AlreadyCancelled_StillThrows()
    {
        var order = NewPendingOrder();
        order.Cancel("Out of stock.");

        Assert.Throws<InvalidOperationException>(() => order.Confirm());
        Assert.Equal(OrderStatus.Cancelled, order.Status);
    }
}
