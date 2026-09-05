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
    public void Cancel_FromPending_TransitionsToCancelled()
    {
        var order = NewPendingOrder();

        order.Cancel();

        Assert.Equal(OrderStatus.Cancelled, order.Status);
    }

    [Fact]
    public void Confirm_AlreadyConfirmed_Throws()
    {
        var order = NewPendingOrder();
        order.Confirm();

        Assert.Throws<InvalidOperationException>(() => order.Confirm());
    }

    [Fact]
    public void Cancel_AlreadyConfirmed_Throws()
    {
        var order = NewPendingOrder();
        order.Confirm();

        Assert.Throws<InvalidOperationException>(() => order.Cancel());
    }
}
