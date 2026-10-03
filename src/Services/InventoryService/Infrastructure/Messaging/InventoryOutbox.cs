using System.Data;
using MassTransit;

namespace InventoryService.Infrastructure.Messaging;

/// <summary>
/// The outbox settings for InventoryService, in one place so that Program.cs and the
/// integration tests (which host the consumers themselves) cannot drift apart.
/// </summary>
public static class InventoryOutbox
{
    public static void Configure(IEntityFrameworkOutboxConfigurator outbox)
    {
        outbox.UsePostgres();

        // MassTransit runs each consumer inside a transaction, SERIALIZABLE unless told
        // otherwise. Under SERIALIZABLE Postgres does not let two transactions update the
        // same row: the second fails with 40001 and has to be retried. ReserveStock
        // updates one row per product, so a burst for the same product (a flash sale, or
        // the backlog delivered when a broker comes back) meant one winner per retry
        // round; the rest exhausted their retries and were parked in an _error queue
        // that nothing reads.
        //
        // READ COMMITTED lets a second writer wait for the row lock instead. That is only
        // safe because every read-modify-write takes the lock first
        // (IInventoryRepository.GetByProductIdForUpdateAsync); without it, READ COMMITTED
        // would let concurrent reservations overwrite each other.
        outbox.IsolationLevel = IsolationLevel.ReadCommitted;
    }
}
