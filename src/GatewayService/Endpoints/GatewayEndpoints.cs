using Microsoft.AspNetCore.Builder;

/// <summary>
/// External HTTP surface of the system (the Gateway is the only service with a published
/// port). Kept out of Program.cs so the entry point is a pure composition root (SRP), and
/// split into files by audience — SubmissionEndpoints (submitter), ApproverEndpoints
/// (approver/controller/auditor), AdminEndpoints (vendor directory / policy config),
/// PubSubHandlers (anonymous Dapr sidecar deliveries) — so this file is just the route
/// map, not a ~580-line grab-bag of every concern at once.
/// </summary>
public static class GatewayEndpoints
{
    public static void MapGatewayEndpoints(this WebApplication app)
    {
        // N1 role map. Submitter surface: drive your own submission through its lifecycle.
        app.MapSubmissionEndpoints();

        // Approver/controller/auditor surface: the escalation queue, decisions, and the
        // dashboards (approver covers the finance personas; admin implies both roles).
        app.MapApproverEndpoints();

        // Admin-only config surface: vendor directory and policy thresholds, proxied to
        // DecisionEngine (ADR 004's "future admin endpoint" gap, now closed).
        app.MapAdminEndpoints();

        // Dapr sidecar deliveries — anonymous on purpose: the sidecar carries no JWT, and
        // these routes are only reachable inside the compose network (M6, single published
        // port notwithstanding, they're not part of the public API surface).
        app.MapPubSubHandlers();
    }
}
