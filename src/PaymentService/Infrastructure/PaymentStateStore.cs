using Dapr.Client;

/// <summary>
/// The saga's durable-state surface (ADR-002), separated from the saga's logic: claim /
/// processed / reservation, each keyed by TrackingId. Same seam the Gateway cut with
/// IDaprStateClient — PaymentProcessor talks to this interface only, so tests exercise the
/// real coordinator logic against an in-memory implementation instead of the old pattern
/// (a nullable DaprClient parameter and a dual-branch if/else inside every state helper).
/// </summary>
public interface IPaymentStateStore
{
    /// <summary>Permanent success record: a completed payment never runs twice, no matter
    /// how many times the invoice.approved event is redelivered.</summary>
    Task<bool> IsProcessedAsync(string trackingId);
    Task MarkProcessedAsync(string trackingId);

    /// <summary>Atomic in-flight claim (Dapr: ETag-conditional write): of two concurrent
    /// deliveries of the same event, exactly one wins. Short-lived — released after the
    /// attempt so a *failed* transfer can legitimately retry later.</summary>
    Task<bool> TryClaimAsync(string trackingId);
    Task ReleaseClaimAsync(string trackingId);

    /// <summary>Reserved amount persisted for the duration of the transfer, so a crash
    /// mid-saga leaves evidence of what needs compensating.</summary>
    Task SaveReservationAsync(string trackingId, decimal amount);
    Task ClearReservationAsync(string trackingId);
}

/// <summary>PaymentService-private key formats — see StateKeys in Shared.Contracts for
/// why only the invoice record itself is shared across services.</summary>
public static class PaymentStateKeys
{
    public static string Processed(string trackingId) => $"invoice-{trackingId}-processed";
    public static string Claim(string trackingId) => $"invoice-{trackingId}-claim";
    public static string Reservation(string trackingId) => $"invoice-{trackingId}-reservation";
}

public class DaprPaymentStateStore : IPaymentStateStore
{
    private readonly DaprClient _daprClient;

    public DaprPaymentStateStore(DaprClient daprClient)
    {
        _daprClient = daprClient;
    }

    public Task<bool> IsProcessedAsync(string trackingId)
        => _daprClient.GetStateAsync<bool>(DaprComponents.StateStore, PaymentStateKeys.Processed(trackingId));

    public Task MarkProcessedAsync(string trackingId)
        => _daprClient.SaveStateAsync(DaprComponents.StateStore, PaymentStateKeys.Processed(trackingId), true);

    public async Task<bool> TryClaimAsync(string trackingId)
    {
        var claimKey = PaymentStateKeys.Claim(trackingId);
        var (claimed, etag) = await _daprClient.GetStateAndETagAsync<bool>(DaprComponents.StateStore, claimKey);
        if (claimed)
            return false;

        // The result matters: losing this ETag race means a concurrent delivery of the
        // same event already holds the claim.
        return await _daprClient.TrySaveStateAsync(DaprComponents.StateStore, claimKey, true, etag);
    }

    public Task ReleaseClaimAsync(string trackingId)
        => _daprClient.DeleteStateAsync(DaprComponents.StateStore, PaymentStateKeys.Claim(trackingId));

    public Task SaveReservationAsync(string trackingId, decimal amount)
        => _daprClient.SaveStateAsync(DaprComponents.StateStore, PaymentStateKeys.Reservation(trackingId), amount);

    public Task ClearReservationAsync(string trackingId)
        => _daprClient.DeleteStateAsync(DaprComponents.StateStore, PaymentStateKeys.Reservation(trackingId));
}
