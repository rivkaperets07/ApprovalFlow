using Microsoft.AspNetCore.Http;

/// <summary>Response shapes shared by more than one endpoint file, so they render
/// identically no matter which handler hits the conflict.</summary>
public static class GatewayResults
{
    public static IResult ConcurrentUpdateConflict(string trackingId)
        => Results.Conflict(new { trackingId, error = "Invoice was modified by another request while this action was in flight — reload and retry." });
}
