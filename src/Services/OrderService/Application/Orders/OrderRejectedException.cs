namespace OrderService.Application.Orders;

/// <summary>
/// An order that is refused for a reason the caller can act on, with the HTTP status that
/// describes it. Mapped to a ProblemDetails response by <see cref="ApiExceptionHandler"/>.
/// </summary>
public sealed class OrderRejectedException : Exception
{
    public int StatusCode { get; }
    public string Title { get; }

    private OrderRejectedException(int statusCode, string title, string detail)
        : base(detail)
    {
        StatusCode = statusCode;
        Title = title;
    }

    public static OrderRejectedException UnknownProduct(Guid productId) =>
        new(StatusCodes.Status422UnprocessableEntity, "Unknown product.",
            $"Product {productId} does not exist.");

    public static OrderRejectedException PriceChanged(Guid productId, decimal submitted, decimal current) =>
        new(StatusCodes.Status409Conflict, "Price has changed.",
            $"Product {productId} costs {current:0.00}, not {submitted:0.00}. Refresh the cart and try again.");

    public static OrderRejectedException PricesUnavailable() =>
        new(StatusCodes.Status503ServiceUnavailable, "Prices are temporarily unavailable.",
            "The order could not be priced right now. Please try again shortly.");
}
