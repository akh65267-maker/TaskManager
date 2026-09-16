namespace OrderService.Infrastructure;

/// <summary>
/// How long a checkout may wait for stock responses, and how often that is
/// checked. Configurable because the useful value differs by environment: a
/// normal checkout resolves in well under a second, so minutes of headroom is
/// generous in production, while tests need seconds to be able to observe it.
/// </summary>
public sealed class CheckoutTimeoutOptions
{
    public const string SectionName = "Checkout";

    /// <summary>
    /// How long after the saga starts before the checkout is abandoned. Must be
    /// comfortably longer than a healthy checkout, since expiring one that is
    /// merely slow cancels an order that would have succeeded.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often to scan for expired sagas. This is the granularity of the
    /// timeout, not the timeout itself - a saga is cancelled up to one interval
    /// after its deadline.
    /// </summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(30);
}
