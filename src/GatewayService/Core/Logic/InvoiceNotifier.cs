using System.Collections.Concurrent;

/// <summary>
/// Notification channel: a one-shot, in-process wake-up per TrackingId so a client
/// holding open <c>GET /notifications/{trackingId}</c> (Server-Sent Events) gets the final
/// decision pushed to it instead of having to keep polling <c>GET /status</c>.
/// <c>GetOrAdd</c> on both sides makes the order race-safe — whichever of Publish/
/// WaitForDecisionAsync runs first creates the slot, the other completes/consumes it — so a
/// decision that lands before the client even opens the connection is never lost.
/// Known trade-off: if nobody ever calls WaitForDecisionAsync for a TrackingId (e.g. any
/// client that only ever uses /status), its entry is never removed. Acceptable for this
/// system's scope (a bounded demo, not a long-lived high-volume service); a real deployment
/// would want a sweep/TTL on top of this.
/// </summary>
public interface IInvoiceNotifier
{
    void Publish(string trackingId, object payload);
    Task<object> WaitForDecisionAsync(string trackingId, CancellationToken cancellationToken);
}

public class InvoiceNotifier : IInvoiceNotifier
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<object>> _waiters = new();

    public void Publish(string trackingId, object payload)
    {
        var tcs = _waiters.GetOrAdd(trackingId, _ => new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously));
        tcs.TrySetResult(payload);
    }

    public async Task<object> WaitForDecisionAsync(string trackingId, CancellationToken cancellationToken)
    {
        var tcs = _waiters.GetOrAdd(trackingId, _ => new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously));
        await using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        try
        {
            return await tcs.Task;
        }
        finally
        {
            _waiters.TryRemove(trackingId, out _);
        }
    }
}
