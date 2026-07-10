using Dapr;
using Dapr.Client;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

/// <summary>
/// Submitter surface (N1: submitter/admin role): drive your own submission through its
/// lifecycle — submit, check status, watch for the decision, and resume after a
/// request-info round trip. Split out of the former single GatewayEndpoints.cs (SRP) so
/// the submitter path, the approver path, and the Dapr-only pub/sub handlers each live
/// somewhere a reader can find without scrolling past the other two.
/// </summary>
public static class SubmissionEndpoints
{
    public static void MapSubmissionEndpoints(this WebApplication app)
    {
        app.MapPost("/submit", SubmitAsync).RequireAuthorization(AuthPolicies.Submitter);
        app.MapGet("/vendors", GetVendorsAsync).RequireAuthorization(AuthPolicies.Submitter);
        app.MapGet("/status/{trackingId}", GetStatusAsync).RequireAuthorization(AuthPolicies.Submitter);
        app.MapGet("/notifications/{trackingId}", NotificationsAsync).RequireAuthorization(AuthPolicies.Submitter);
        app.MapPost("/provide-info/{trackingId}", ProvideInfoAsync).RequireAuthorization(AuthPolicies.Submitter);
    }

    private static async Task<IResult> SubmitAsync([FromBody] InvoicePayload invoice, DaprClient daprClient, ISubmissionStore submissionStore, IConfiguration config, ILogger<Program> logger)
    {
        if (invoice is null || string.IsNullOrWhiteSpace(invoice.Vendor) || invoice.TotalAmount <= 0)
        {
            logger.LogWarning("Invalid submission received.");
            return Results.BadRequest(new { error = "Vendor, category and TotalAmount must be provided." });
        }

        invoice.TrackingId ??= Guid.NewGuid().ToString();
        invoice.Status = InvoiceStatus.Pending;
        invoice.Reason = "Submission received and published for decision.";
        invoice.SubmittedAt = DateTimeOffset.UtcNow;
        // Always Gateway-minted, never caller-supplied: DecisionEngine keys its
        // at-least-once dedup on this (see SubmissionAttemptId on InvoicePayload).
        invoice.SubmissionAttemptId = Guid.NewGuid().ToString();

        // Every log line for the rest of this request carries CorrelationId as a
        // structured field (M14) without repeating it as a message argument each time.
        using var _ = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = invoice.TrackingId });

        // N4: same id, now on the trace too — this is the root of the whole distributed
        // trace for this invoice (Gateway -> DecisionEngine -> PaymentService), so tagging
        // it here means every span downstream that shares this trace can be found by
        // searching correlation_id in Jaeger, the same value already searchable in logs.
        Activity.Current?.SetTag("correlation_id", invoice.TrackingId);

        var alreadySubmitted = await submissionStore.HasBeenSubmittedAsync(invoice.TrackingId);
        if (alreadySubmitted)
        {
            logger.LogWarning("Duplicate submission ignored for invoice {TrackingId}.", invoice.TrackingId);
            return Results.Accepted($"/status/{invoice.TrackingId}", new { invoice.TrackingId });
        }

        // M10 backstop: catches an accidental repeat even when the caller doesn't reuse the
        // same TrackingId and doesn't supply an InvoiceNumber for GLOBAL-DUP to key off —
        // e.g. an API client minting a fresh TrackingId per call, or a double-click that
        // slips past the UI's guard. Short time window (60s) so it never blocks two people
        // legitimately expensing the same vendor/amount/category later.
        var recentDuplicateOf = await RecentSubmissionGuard.TryClaimAsync(daprClient, DaprComponents.StateStore, invoice.Vendor, invoice.TotalAmount, invoice.Category, invoice.Notes, invoice.TrackingId);
        if (recentDuplicateOf is not null)
        {
            logger.LogWarning("Submission treated as an accidental repeat of invoice {OriginalTrackingId} (same vendor/amount/category/notes within 60s).", recentDuplicateOf);
            return Results.Accepted($"/status/{recentDuplicateOf}", new { TrackingId = recentDuplicateOf });
        }

        // GLOBAL-DUP (docs/policy.md): an exact repeat of Vendor + InvoiceNumber +
        // TotalAmount is rejected outright — no second payment. Only applies when
        // InvoiceNumber is supplied (it's optional; see InvoicePayload for why this alone
        // isn't F3/M10's real double-payment protection). Checked before GLOBAL-FRAUD since
        // an exact three-field match is a far more certain signal than the round-number
        // heuristic.
        if (!string.IsNullOrWhiteSpace(invoice.InvoiceNumber))
        {
            var isDuplicateInvoice = await DuplicateInvoiceGuard.IsDuplicateAsync(daprClient, DaprComponents.StateStore, invoice.Vendor, invoice.InvoiceNumber, invoice.TotalAmount);
            if (isDuplicateInvoice)
            {
                invoice.Status = InvoiceStatus.Duplicate;
                invoice.Reason = $"Rejected: invoice '{invoice.InvoiceNumber}' from '{invoice.Vendor}' for {invoice.TotalAmount:C} was already submitted (GLOBAL-DUP).";
                invoice.DecidedBy = DecidedBy.System;

                // Not added to the escalation queue — this is an outright reject, not a
                // human-review item (F6: no rubber-stamping a call the router can make itself).
                await PersistSubmissionAsync(daprClient, submissionStore, invoice);

                logger.LogWarning("Invoice {TrackingId} rejected as a duplicate of invoice '{InvoiceNumber}' (GLOBAL-DUP).", invoice.TrackingId, invoice.InvoiceNumber);
                return Results.Accepted($"/status/{invoice.TrackingId}", new { invoice.TrackingId });
            }
        }

        // GLOBAL-FRAUD (docs/policy.md): a round-number amount to a vendor the company has
        // never dealt with before is a fraud-pattern signal. Judged solely on this
        // submission's own attributes, never by comparing it to any other submission —
        // two different people legitimately expensing the same known vendor for the same
        // amount must never trip this.
        if (FraudGuard.IsLikelySuspicious(invoice.Vendor, invoice.TotalAmount, VendorDirectory.LoadKnownVendors(config)))
        {
            invoice.Status = InvoiceStatus.Escalated;
            invoice.Reason = $"Blocked: round-number amount to an unrecognized vendor '{invoice.Vendor}' (GLOBAL-FRAUD guardrail).";
            invoice.DecidedBy = DecidedBy.System;

            await PersistSubmissionAsync(daprClient, submissionStore, invoice, addToEscalationQueue: true);

            logger.LogWarning("Invoice {TrackingId} blocked by GLOBAL-FRAUD guardrail (round-number amount, unrecognized vendor).", invoice.TrackingId);
            return Results.Accepted($"/status/{invoice.TrackingId}", new { invoice.TrackingId });
        }

        await PersistSubmissionAsync(daprClient, submissionStore, invoice);
        await daprClient.PublishEventAsync(DaprComponents.PubSub, Topics.InvoiceSubmitted, invoice);

        logger.LogInformation("Invoice submitted {TrackingId} by {Vendor}.", invoice.TrackingId, invoice.Vendor);
        return Results.Accepted($"/status/{invoice.TrackingId}", new { invoice.TrackingId });
    }

    // Every submission outcome persists identically: the invoice record, the TrackingId
    // idempotency flag, and membership in the all-submissions index (F8's dashboard input);
    // gate-escalated outcomes (GLOBAL-FRAUD) additionally join the escalation queue. One
    // helper so the three SubmitAsync outcomes can't drift apart.
    private static async Task PersistSubmissionAsync(DaprClient daprClient, ISubmissionStore submissionStore, InvoicePayload invoice, bool addToEscalationQueue = false)
    {
        await daprClient.SaveStateAsync(DaprComponents.StateStore, StateKeys.Invoice(invoice.TrackingId!), invoice);
        await submissionStore.MarkSubmittedAsync(invoice.TrackingId!);
        await StateIndex.AddAsync(daprClient, DaprComponents.StateStore, GatewayIndexKeys.Submitted, invoice.TrackingId!);
        if (addToEscalationQueue)
        {
            await StateIndex.AddAsync(daprClient, DaprComponents.StateStore, GatewayIndexKeys.Escalated, invoice.TrackingId!);
        }
    }

    // Proxies DecisionEngine's /vendors via Dapr's synchronous service invocation (M5's
    // remaining building block — everything else in this system talks pub/sub or state).
    // The sidecar handles service discovery/mTLS/retries; the Gateway never needs
    // DecisionEngine's network address, only its Dapr app-id.
    private static async Task<IResult> GetVendorsAsync(DaprClient daprClient, Bulkhead decisionEngineBulkhead, ILogger<Program> logger)
    {
        try
        {
            // N3 bulkhead: caps concurrent Gateway->DecisionEngine calls so a slow/overloaded
            // DecisionEngine can't exhaust Gateway's own resources (see Bulkhead.cs).
            var vendors = await decisionEngineBulkhead.ExecuteAsync(
                () => daprClient.InvokeMethodAsync<List<VendorEntry>>(HttpMethod.Get, "decision-engine", "vendors"));
            return Results.Ok(vendors);
        }
        catch (BulkheadRejectedException)
        {
            logger.LogWarning("Rejected a vendor-directory read: the DecisionEngine bulkhead is already at capacity.");
            return Results.Problem(detail: "The vendor directory is temporarily unavailable (too many concurrent DecisionEngine calls).", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex)
        {
            // 503, not an empty 200: the UI (and any API client) must be able to tell "no
            // vendors are configured" apart from "DecisionEngine is unreachable" — masking
            // an outage as an empty directory sends submitters chasing the wrong problem.
            logger.LogWarning(ex, "Could not reach DecisionEngine for the vendor directory.");
            return Results.Problem(detail: "The vendor directory is temporarily unavailable (DecisionEngine unreachable).", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> GetStatusAsync([FromRoute] string trackingId, DaprClient daprClient, ILogger<Program> logger)
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
        logger.LogInformation("Status requested for invoice {TrackingId}.", trackingId);
        return Results.Ok(new
        {
            invoice.TrackingId,
            invoice.Status,
            invoice.Reason,
            invoice.Vendor,
            invoice.Category,
            invoice.TotalAmount,
            invoice.DecidedBy,
            invoice.AiSuggestedCategory,
            invoice.AiConfidence,
            invoice.AiPolicyRulesCited,
            invoice.PaymentStatus,
            invoice.PaymentMessage,
            invoice.SubmittedAt
        });
    }

    // M8: the notification channel — a client opens this right after /submit and gets the
    // decision pushed to it (Server-Sent Events) instead of having to poll /status. Checks
    // current state first in case the decision already landed before the connection opened
    // (the Stub AI decides in well under a second); otherwise waits on IInvoiceNotifier,
    // which PubSubHandlers.HandleInvoiceDecidedIndexAsync publishes to. Sends exactly one
    // event, for the decision itself — not every later lifecycle change (a human's
    // approve/reject after an escalation, or the eventual payment outcome), which /status
    // still covers.
    private static async Task NotificationsAsync(HttpContext context, [FromRoute] string trackingId, DaprClient daprClient, IInvoiceNotifier notifier, ILogger<Program> logger)
    {
        if (string.IsNullOrWhiteSpace(trackingId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        using var _ = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = trackingId });
        var cancellationToken = context.RequestAborted;
        var invoice = await daprClient.GetStateAsync<InvoicePayload>(DaprComponents.StateStore, StateKeys.Invoice(trackingId));

        object result;
        if (invoice is not null && invoice.Status != InvoiceStatus.Pending)
        {
            result = new { invoice.TrackingId, invoice.Status };
        }
        else
        {
            try
            {
                result = await notifier.WaitForDecisionAsync(trackingId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return; // client disconnected before a decision arrived
            }
        }

        await context.Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(result)}\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);

        logger.LogInformation("Notification delivered for invoice {TrackingId} via SSE.", trackingId);
    }

    // F5's resume half: the submitter fills in whatever was missing and this puts the
    // invoice back through the exact same evaluation path a first-time submission takes
    // (invoice.submitted -> DecisionEngine), using the same TrackingId so the audit trail
    // and payment flow never see two records for one expense. Published directly rather
    // than through /submit so it isn't swallowed by that endpoint's F3 idempotency guard —
    // this is a deliberate re-evaluation of an existing invoice, not a possible duplicate.
    private static async Task<IResult> ProvideInfoAsync([FromRoute] string trackingId, [FromBody] MoreInfoUpdate? update, DaprClient daprClient, ILogger<Program> logger)
    {
        if (string.IsNullOrWhiteSpace(trackingId))
        {
            return Results.BadRequest(new { error = "trackingId is required." });
        }

        using var _ = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = trackingId });

        // ETag alongside the record: the save below is conditional on it, so a concurrent
        // approver action (approve/reject/request-info are all valid on NeedsInfo) can't be
        // silently overwritten by this re-submission — one side gets a 409 instead.
        var (invoice, etag) = await daprClient.GetStateAndETagAsync<InvoicePayload>(DaprComponents.StateStore, StateKeys.Invoice(trackingId));
        if (invoice is null || string.IsNullOrEmpty(invoice.TrackingId))
        {
            return Results.NotFound(new { trackingId, message = "Invoice not found." });
        }

        if (invoice.Status != InvoiceStatus.NeedsInfo)
        {
            return Results.BadRequest(new { error = $"Invoice {trackingId} is not awaiting more information (current status: {invoice.Status})." });
        }

        if (update is not null)
        {
            if (update.Notes is not null) invoice.Notes = update.Notes;
            if (update.LineItems is not null) invoice.LineItems = update.LineItems;
            if (update.BusinessJustification is not null) invoice.BusinessJustification = update.BusinessJustification;
            if (update.ClientName is not null) invoice.ClientName = update.ClientName;
            if (update.Currency is not null) invoice.Currency = update.Currency;
            if (update.TripId is not null) invoice.TripId = update.TripId;
            if (update.IsPremiumTravel.HasValue) invoice.IsPremiumTravel = update.IsPremiumTravel.Value;
        }

        invoice.Status = InvoiceStatus.Pending;
        invoice.Reason = "Additional information provided; re-evaluating.";
        // Fresh attempt id: this is a *deliberate* re-evaluation, and DecisionEngine's
        // at-least-once dedup must not mistake it for a redelivery of the original event.
        invoice.SubmissionAttemptId = Guid.NewGuid().ToString();

        // Publish only after the conditional write wins — the losing side must not push a
        // stale record back through the evaluation pipeline.
        if (!await daprClient.TrySaveStateAsync(DaprComponents.StateStore, StateKeys.Invoice(trackingId), invoice, etag))
        {
            return GatewayResults.ConcurrentUpdateConflict(trackingId);
        }

        await daprClient.PublishEventAsync(DaprComponents.PubSub, Topics.InvoiceSubmitted, invoice);

        logger.LogInformation("Invoice {TrackingId} resubmitted with additional info for re-evaluation.", trackingId);
        return Results.Accepted($"/status/{trackingId}", new { invoice.TrackingId });
    }
}
