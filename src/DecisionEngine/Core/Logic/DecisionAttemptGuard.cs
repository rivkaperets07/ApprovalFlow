using Dapr.Client;

namespace DecisionEngine.Core.Logic;

/// <summary>
/// Idempotency guard for invoice.submitted deliveries. Dapr pub/sub is at-least-once, and
/// evaluating the same delivery twice is not harmless: it costs a second AI call and, for
/// Travel, increments the cumulative trip total a second time (PolicyEngine's
/// EvaluateTravelAsync), silently eating the trip cap. Same two-key design as
/// PaymentProcessor (ADR 002): a permanent "decided" marker per SubmissionAttemptId written
/// only after the decision is saved and published, plus a short-lived ETag-conditional claim
/// so two concurrent deliveries of the same event can't both proceed. A deliberate
/// re-evaluation (the Gateway's provide-info flow) carries a fresh SubmissionAttemptId, so
/// it is never mistaken for a redelivery.
/// </summary>
public static class DecisionAttemptGuard
{
    public static string BuildDecidedKey(string attemptId) => $"decide-attempt-{attemptId}-done";
    public static string BuildClaimKey(string attemptId) => $"decide-attempt-{attemptId}-claim";

    /// <summary>
    /// Returns true if this delivery won the right to evaluate the attempt; false if the
    /// attempt was already decided (redelivery after completion) or is currently being
    /// evaluated by a concurrent delivery (lost the ETag race). Callers that get true must
    /// pair it with <see cref="MarkDecidedAsync"/> on success and
    /// <see cref="ReleaseClaimAsync"/> in a finally, so a crashed evaluation can be retried
    /// by the next redelivery instead of being locked out forever.
    /// </summary>
    public static async Task<bool> TryBeginAsync(DaprClient daprClient, string storeName, string attemptId)
    {
        if (await daprClient.GetStateAsync<bool>(storeName, BuildDecidedKey(attemptId)))
            return false;

        var claimKey = BuildClaimKey(attemptId);
        var (claimed, etag) = await daprClient.GetStateAndETagAsync<bool>(storeName, claimKey);
        if (claimed)
            return false;

        // The TrySaveStateAsync result matters: losing this ETag race means another
        // delivery of the same event is already evaluating — do not proceed.
        return await daprClient.TrySaveStateAsync(storeName, claimKey, true, etag);
    }

    public static Task MarkDecidedAsync(DaprClient daprClient, string storeName, string attemptId)
        => daprClient.SaveStateAsync(storeName, BuildDecidedKey(attemptId), true);

    public static Task ReleaseClaimAsync(DaprClient daprClient, string storeName, string attemptId)
        => daprClient.DeleteStateAsync(storeName, BuildClaimKey(attemptId));
}
