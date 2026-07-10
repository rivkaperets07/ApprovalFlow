using Dapr.Client;

/// <summary>
/// Small helper to keep a set of tracking ids in a single Dapr state key (e.g. "which
/// invoices are currently escalated"), since the Dapr state API has no native list/scan
/// operation. Reads and writes are ETag-guarded with a short retry loop so concurrent
/// updates from different requests don't silently overwrite each other.
/// Known trade-off: GatewayIndexKeys.Submitted (unlike Escalated, which shrinks again as
/// items are decided) only ever grows — every submission joins it and nothing ever
/// removes an entry. /stats reads the whole thing on every call (via
/// ApproverEndpoints.LoadInvoicesAsync's bulk read), so both the size of that one Redis
/// value and the dashboard's load time scale with all-time submission count, not recent
/// activity. Acceptable for this system's scope (a bounded demo, not a long-lived
/// high-volume service); a real deployment would page this — e.g. a per-day index plus a
/// running total for the counts /stats reports, so old days are read only if requested.
/// </summary>
public static class StateIndex
{
    private const int MaxAttempts = 5;

    public static async Task AddAsync(DaprClient daprClient, string storeName, string indexKey, string trackingId)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var (list, etag) = await daprClient.GetStateAndETagAsync<List<string>>(storeName, indexKey);
            list ??= [];
            if (list.Contains(trackingId))
                return;

            list.Add(trackingId);
            if (await daprClient.TrySaveStateAsync(storeName, indexKey, list, etag))
                return;
        }
    }

    public static async Task RemoveAsync(DaprClient daprClient, string storeName, string indexKey, string trackingId)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var (list, etag) = await daprClient.GetStateAndETagAsync<List<string>>(storeName, indexKey);
            if (list is null || !list.Remove(trackingId))
                return;

            if (await daprClient.TrySaveStateAsync(storeName, indexKey, list, etag))
                return;
        }
    }

    public static async Task<List<string>> GetAllAsync(DaprClient daprClient, string storeName, string indexKey)
    {
        var list = await daprClient.GetStateAsync<List<string>>(storeName, indexKey);
        return list ?? [];
    }
}
