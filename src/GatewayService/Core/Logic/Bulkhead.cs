/// <summary>
/// Caps how many concurrent calls to one downstream dependency are in flight, so that
/// dependency being slow or overloaded can't exhaust Gateway's own thread/connection pool
/// and drag down unrelated requests with it — the isolation this borrows its name from
/// (a ship's bulkheads keep one flooded compartment from sinking the whole hull). Rejects
/// fast (<see cref="BulkheadRejectedException"/>) rather than queuing past the cap: a
/// caller stuck waiting behind an unbounded queue for an overloaded dependency is exactly
/// the failure mode a bulkhead exists to prevent — callers should see "busy, retry" now,
/// not a request that eventually times out anyway after tying up a thread for the wait.
/// </summary>
public sealed class Bulkhead
{
    private readonly SemaphoreSlim _semaphore;

    public Bulkhead(int maxConcurrentCalls)
    {
        if (maxConcurrentCalls < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentCalls), "A bulkhead must allow at least one concurrent call.");

        _semaphore = new SemaphoreSlim(maxConcurrentCalls, maxConcurrentCalls);
    }

    public async Task<T> ExecuteAsync<T>(Func<Task<T>> action)
    {
        if (!await _semaphore.WaitAsync(TimeSpan.Zero))
            throw new BulkheadRejectedException();

        try
        {
            return await action();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task ExecuteAsync(Func<Task> action)
    {
        if (!await _semaphore.WaitAsync(TimeSpan.Zero))
            throw new BulkheadRejectedException();

        try
        {
            await action();
        }
        finally
        {
            _semaphore.Release();
        }
    }
}

/// <summary>Thrown when a <see cref="Bulkhead"/> is already at its concurrency cap. Distinct
/// from any exception the wrapped call itself could throw, so callers can tell "this
/// dependency is genuinely unreachable" apart from "we chose not to pile on more load".</summary>
public sealed class BulkheadRejectedException : Exception
{
    public BulkheadRejectedException() : base("Bulkhead capacity exceeded; rejected instead of queuing.")
    {
    }
}
