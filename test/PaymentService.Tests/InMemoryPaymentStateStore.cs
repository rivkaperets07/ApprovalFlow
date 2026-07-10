using System.Collections.Concurrent;
using System.Threading.Tasks;

/// <summary>
/// In-memory IPaymentStateStore for tests. Lives in the test project on purpose — the
/// production assembly carries no test scaffolding (it used to, as a nullable-DaprClient
/// dual implementation inside PaymentProcessor itself). TryAdd provides the same atomic
/// claim semantics as the Dapr ETag-conditional write, so the concurrency tests exercise
/// the real race.
/// </summary>
public class InMemoryPaymentStateStore : IPaymentStateStore
{
    private readonly ConcurrentDictionary<string, bool> _claimed = new();
    private readonly ConcurrentDictionary<string, bool> _completed = new();
    private readonly ConcurrentDictionary<string, decimal> _reservations = new();

    public Task<bool> IsProcessedAsync(string trackingId)
        => Task.FromResult(_completed.ContainsKey(trackingId));

    public Task MarkProcessedAsync(string trackingId)
    {
        _completed[trackingId] = true;
        return Task.CompletedTask;
    }

    public Task<bool> TryClaimAsync(string trackingId)
        => Task.FromResult(_claimed.TryAdd(trackingId, true));

    public Task ReleaseClaimAsync(string trackingId)
    {
        _claimed.TryRemove(trackingId, out _);
        return Task.CompletedTask;
    }

    public Task SaveReservationAsync(string trackingId, decimal amount)
    {
        _reservations[trackingId] = amount;
        return Task.CompletedTask;
    }

    public Task ClearReservationAsync(string trackingId)
    {
        _reservations.TryRemove(trackingId, out _);
        return Task.CompletedTask;
    }
}
