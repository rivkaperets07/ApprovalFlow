using Dapr;
using Dapr.Client;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

/// <summary>
/// Approver/controller/auditor surface (approver/admin role): the escalation queue,
/// the three manual decisions, the dashboards, and the per-invoice audit trail. Split out
/// of the former single GatewayEndpoints.cs (SRP) — see SubmissionEndpoints for the
/// submitter half and PubSubHandlers for the Dapr-only deliveries.
/// </summary>
public static class ApproverEndpoints
{
    public static void MapApproverEndpoints(this WebApplication app)
    {
        app.MapGet("/escalations", GetEscalationsAsync).RequireAuthorization(AuthPolicies.Approver);
        app.MapGet("/stats", GetStatsAsync).RequireAuthorization(AuthPolicies.Approver);
        app.MapGet("/audit/{trackingId}", GetAuditTrailAsync).RequireAuthorization(AuthPolicies.Approver);
        app.MapPost("/approve/{trackingId}", ApproveAsync).RequireAuthorization(AuthPolicies.Approver);
        app.MapPost("/reject/{trackingId}", RejectAsync).RequireAuthorization(AuthPolicies.Approver);
        app.MapPost("/request-info/{trackingId}", RequestInfoAsync).RequireAuthorization(AuthPolicies.Approver);
    }

    // The auditor's complete decision trail for one item, linked by its TrackingId as
    // the correlation id (the same id every log line across every service tags itself
    // with). Unlike /status — a curated, plain-language submitter view — this returns
    // the full stored record as-is: the extracted data the submitter/AI supplied
    // (LineItems, TripId, Currency, BusinessJustification, ClientName, IsPremiumTravel,
    // InvoiceNumber), the AI's reasoning and confidence, who made the final call
    // (DecidedBy: AI / System / Human — see DecidedBy.cs), and the payment outcome once
    // PubSubHandlers.HandlePaymentCompletedAsync has merged it in. No projection here on
    // purpose — an auditor should not have to guess which fields were left out.
    private static async Task<IResult> GetAuditTrailAsync([FromRoute] string trackingId, DaprClient daprClient, ILogger<Program> logger)
    {
        if (string.IsNullOrWhiteSpace(trackingId))
        {
            return Results.BadRequest(new { error = "trackingId is required." });
        }

        var invoice = await daprClient.GetStateAsync<InvoicePayload>(DaprComponents.StateStore, StateKeys.Invoice(trackingId));
        if (invoice is null || string.IsNullOrEmpty(invoice.TrackingId))
        {
            return Results.NotFound(new { trackingId, message = "Invoice not found." });
        }

        using var _ = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = trackingId });
        logger.LogInformation("Audit trail pulled for invoice {TrackingId}.", trackingId);
        return Results.Ok(invoice);
    }

    // Queue of only the items the system escalated, so an approver never has to
    // rubber-stamp what the router already handled on its own.
    private static async Task<IResult> GetEscalationsAsync(DaprClient daprClient)
    {
        var trackingIds = await StateIndex.GetAllAsync(daprClient, DaprComponents.StateStore, GatewayIndexKeys.Escalated);
        var invoices = await LoadInvoicesAsync(daprClient, trackingIds);

        var items = invoices
            .Where(invoice => invoice.Status == InvoiceStatus.Escalated)
            .Select(invoice => new
            {
                invoice.TrackingId,
                invoice.Vendor,
                invoice.Category,
                invoice.AiSuggestedCategory,
                invoice.AiConfidence,
                invoice.AiPolicyRulesCited,
                invoice.TotalAmount,
                invoice.Reason,
                invoice.ReceiptImageDataUri,
                invoice.ReceiptVerificationVerdict,
                invoice.ReceiptVerificationConfidence
            });
        return Results.Ok(items);
    }

    // Throughput and auto- vs human-approved money, for the controller dashboard.
    private static async Task<IResult> GetStatsAsync(DaprClient daprClient)
    {
        var trackingIds = await StateIndex.GetAllAsync(daprClient, DaprComponents.StateStore, GatewayIndexKeys.Submitted);
        var invoices = await LoadInvoicesAsync(daprClient, trackingIds);

        int totalSubmitted = 0, autoApproved = 0, humanApproved = 0, escalatedPending = 0, rejected = 0, duplicateRejected = 0, needsInfo = 0;
        decimal autoApprovedAmount = 0, humanApprovedAmount = 0;

        foreach (var invoice in invoices)
        {
            totalSubmitted++;
            switch (invoice.Status)
            {
                case InvoiceStatus.Approved when invoice.DecidedBy == DecidedBy.Ai:
                    autoApproved++;
                    autoApprovedAmount += invoice.TotalAmount;
                    break;
                case InvoiceStatus.Approved:
                    humanApproved++;
                    humanApprovedAmount += invoice.TotalAmount;
                    break;
                case InvoiceStatus.Escalated:
                    escalatedPending++;
                    break;
                case InvoiceStatus.Rejected:
                    rejected++;
                    break;
                case InvoiceStatus.Duplicate:
                    duplicateRejected++;
                    break;
                case InvoiceStatus.NeedsInfo:
                    needsInfo++;
                    break;
            }
        }

        return Results.Ok(new
        {
            totalSubmitted,
            autoApproved,
            humanApproved,
            escalatedPending,
            needsInfo,
            rejected,
            duplicateRejected,
            autoApprovedAmount,
            humanApprovedAmount,
            autoApprovalRate = totalSubmitted == 0 ? 0 : Math.Round((double)autoApproved / totalSubmitted, 2)
        });
    }

    // One round trip to the state store instead of one GetStateAsync per TrackingId in the
    // index: /stats and /escalations both scan the *entire* submitted/escalated index on
    // every call, so at any real volume the N+1 pattern this replaced was the dominant cost
    // of loading either dashboard. GetBulkStateAsync still issues one read per key under
    // the hood, but Dapr fans them out concurrently instead of the caller awaiting them
    // one at a time.
    private static async Task<List<InvoicePayload>> LoadInvoicesAsync(DaprClient daprClient, IReadOnlyList<string> trackingIds)
    {
        if (trackingIds.Count == 0)
            return [];

        var keys = trackingIds.Select(StateKeys.Invoice).ToList();
        var items = await daprClient.GetBulkStateAsync<InvoicePayload>(DaprComponents.StateStore, keys, parallelism: null);
        return items.Where(item => item.Value is not null).Select(item => item.Value).ToList();
    }

    // Shared load+validation for the three approver actions below: same trackingId checks,
    // and the ApproverActionGuard state machine — acting on an in-flight or already-final
    // invoice is a 409, not a silent state rewrite (see the guard for the full rationale).
    // The record's ETag rides along so the caller's save can be conditional on it: two
    // approvers acting on the same item at once must not silently last-write-wins each
    // other (one approving, one rejecting — both "succeeding").
    private static async Task<(InvoicePayload? Invoice, string Etag, IResult? Error)> LoadReviewableInvoiceAsync(string trackingId, DaprClient daprClient)
    {
        if (string.IsNullOrWhiteSpace(trackingId))
        {
            return (null, string.Empty, Results.BadRequest(new { error = "trackingId is required." }));
        }

        var (invoice, etag) = await daprClient.GetStateAndETagAsync<InvoicePayload>(DaprComponents.StateStore, StateKeys.Invoice(trackingId));
        if (invoice is null || string.IsNullOrEmpty(invoice.TrackingId))
        {
            return (null, string.Empty, Results.NotFound(new { trackingId, message = "Invoice not found." }));
        }

        if (!ApproverActionGuard.CanActOn(invoice.Status))
        {
            return (null, string.Empty, Results.Conflict(new { trackingId, error = $"Invoice is '{invoice.Status}' — approver actions apply only to items awaiting review (Escalated or NeedsInfo)." }));
        }

        return (invoice, etag, null);
    }

    private static async Task<IResult> ApproveAsync([FromRoute] string trackingId, DaprClient daprClient, ILogger<Program> logger)
    {
        var (invoice, etag, error) = await LoadReviewableInvoiceAsync(trackingId, daprClient);
        if (invoice is null)
        {
            return error!;
        }

        using var _ = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = trackingId });
        invoice.Status = InvoiceStatus.Approved;
        invoice.Reason = "Manually approved by reviewer.";
        invoice.DecidedBy = DecidedBy.Human;

        // Events fire only after the ETag-conditional write wins: the losing side of a
        // concurrent approve/reject must not publish invoice.approved (and trigger a
        // payment) for a record that now says Rejected.
        if (!await daprClient.TrySaveStateAsync(DaprComponents.StateStore, StateKeys.Invoice(trackingId), invoice, etag))
        {
            return GatewayResults.ConcurrentUpdateConflict(trackingId);
        }

        await daprClient.PublishEventAsync(DaprComponents.PubSub, Topics.InvoiceDecided, new DecisionResult { TrackingId = trackingId, Approved = true, Reason = invoice.Reason, DecidedBy = DecidedBy.Human });
        await daprClient.PublishEventAsync(DaprComponents.PubSub, Topics.InvoiceApproved, invoice);

        logger.LogInformation("Invoice {TrackingId} approved manually.", trackingId);
        return Results.Ok(invoice);
    }

    private static async Task<IResult> RejectAsync([FromRoute] string trackingId, DaprClient daprClient, ILogger<Program> logger)
    {
        var (invoice, etag, error) = await LoadReviewableInvoiceAsync(trackingId, daprClient);
        if (invoice is null)
        {
            return error!;
        }

        using var _ = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = trackingId });
        invoice.Status = InvoiceStatus.Rejected;
        invoice.Reason = "Manually rejected by reviewer.";
        invoice.DecidedBy = DecidedBy.Human;

        if (!await daprClient.TrySaveStateAsync(DaprComponents.StateStore, StateKeys.Invoice(trackingId), invoice, etag))
        {
            return GatewayResults.ConcurrentUpdateConflict(trackingId);
        }

        await daprClient.PublishEventAsync(DaprComponents.PubSub, Topics.InvoiceDecided, new DecisionResult { TrackingId = trackingId, Approved = false, Reason = invoice.Reason, DecidedBy = DecidedBy.Human });

        logger.LogInformation("Invoice {TrackingId} rejected manually.", trackingId);
        return Results.Ok(invoice);
    }

    // Third leg of the approver's one-action set (alongside approve/reject): park the
    // invoice on the submitter instead of deciding outright. Publishing invoice.decided with
    // the (now non-Escalated) status already saved lets PubSubHandlers.HandleInvoiceDecidedIndexAsync
    // drop it out of the escalation queue exactly the same way approve/reject do — no
    // separate index-maintenance logic needed for this third action.
    private static async Task<IResult> RequestInfoAsync([FromRoute] string trackingId, [FromBody] RequestInfoBody? body, DaprClient daprClient, ILogger<Program> logger)
    {
        var (invoice, etag, error) = await LoadReviewableInvoiceAsync(trackingId, daprClient);
        if (invoice is null)
        {
            return error!;
        }

        using var _ = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = trackingId });
        var message = string.IsNullOrWhiteSpace(body?.Message)
            ? "Reviewer requested more information before this can be decided."
            : body!.Message!.Trim();

        invoice.Status = InvoiceStatus.NeedsInfo;
        invoice.Reason = message;
        invoice.DecidedBy = DecidedBy.Human;

        if (!await daprClient.TrySaveStateAsync(DaprComponents.StateStore, StateKeys.Invoice(trackingId), invoice, etag))
        {
            return GatewayResults.ConcurrentUpdateConflict(trackingId);
        }

        await daprClient.PublishEventAsync(DaprComponents.PubSub, Topics.InvoiceDecided, new DecisionResult { TrackingId = trackingId, Approved = false, Reason = message, DecidedBy = DecidedBy.Human });

        logger.LogInformation("Invoice {TrackingId} sent back to the submitter for more information.", trackingId);
        return Results.Ok(invoice);
    }
}

// What a reviewer sends along with a "request more info" action — a plain-language
// note for the submitter, distinct from the InvoicePayload contract itself.
public class RequestInfoBody
{
    public string? Message { get; set; }
}
