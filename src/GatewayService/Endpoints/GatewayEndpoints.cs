using Dapr;
using Dapr.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

/// <summary>
/// External HTTP surface of the system (the Gateway is the only service with a published
/// port). Kept out of Program.cs so the entry point is a pure composition root (SRP):
/// this file owns request handling, Program.cs owns wiring.
/// </summary>
public static class GatewayEndpoints
{
    private const string StateStoreName = "statestore";
    private const string PubSubName = "pubsub";
    private const string SubmittedTopic = "invoice.submitted";
    private const string SubmittedIndexKey = "index-submitted";
    private const string EscalatedIndexKey = "index-escalated";

    public static void MapGatewayEndpoints(this WebApplication app)
    {
        app.MapPost("/submit", SubmitAsync);
        app.MapGet("/vendors", GetVendorsAsync);
        app.MapGet("/status/{trackingId}", GetStatusAsync);
        app.MapPost("/payment-completed", HandlePaymentCompletedAsync);
        app.MapGet("/escalations", GetEscalationsAsync);
        app.MapGet("/stats", GetStatsAsync);
        app.MapPost("/approve/{trackingId}", ApproveAsync);
        app.MapPost("/reject/{trackingId}", RejectAsync);
        app.MapPost("/invoice-decided-index", HandleInvoiceDecidedIndexAsync);
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

        var alreadySubmitted = await submissionStore.HasBeenSubmittedAsync(invoice.TrackingId);
        if (alreadySubmitted)
        {
            logger.LogWarning("{CorrelationId} Duplicate submission ignored for invoice {TrackingId}.", invoice.TrackingId, invoice.TrackingId);
            return Results.Accepted($"/status/{invoice.TrackingId}", new { invoice.TrackingId });
        }

        // GLOBAL-DUP (docs/policy.md): an exact repeat of Vendor + InvoiceNumber +
        // TotalAmount is rejected outright — no second payment. Only applies when
        // InvoiceNumber is supplied (it's optional; see InvoicePayload for why this alone
        // isn't F3/M10's real double-payment protection). Checked before GLOBAL-FRAUD since
        // an exact three-field match is a far more certain signal than the round-number
        // heuristic.
        if (!string.IsNullOrWhiteSpace(invoice.InvoiceNumber))
        {
            var isDuplicateInvoice = await DuplicateInvoiceGuard.IsDuplicateAsync(daprClient, StateStoreName, invoice.Vendor, invoice.InvoiceNumber, invoice.TotalAmount);
            if (isDuplicateInvoice)
            {
                invoice.Status = InvoiceStatus.Duplicate;
                invoice.Reason = $"Rejected: invoice '{invoice.InvoiceNumber}' from '{invoice.Vendor}' for {invoice.TotalAmount:C} was already submitted (GLOBAL-DUP).";
                invoice.DecidedBy = DecidedBy.System;

                await daprClient.SaveStateAsync(StateStoreName, GetStateKey(invoice.TrackingId), invoice);
                await submissionStore.MarkSubmittedAsync(invoice.TrackingId);
                await StateIndex.AddAsync(daprClient, StateStoreName, SubmittedIndexKey, invoice.TrackingId);
                // Not added to the escalated index — this is an outright reject, not a
                // human-review item (F6: no rubber-stamping a call the router can make itself).

                logger.LogWarning("{CorrelationId} Invoice {TrackingId} rejected as a duplicate of invoice '{InvoiceNumber}' (GLOBAL-DUP).", invoice.TrackingId, invoice.TrackingId, invoice.InvoiceNumber);
                return Results.Accepted($"/status/{invoice.TrackingId}", new { invoice.TrackingId });
            }
        }

        // GLOBAL-FRAUD (docs/policy.md): a round-number amount to a vendor the company has
        // never dealt with before is a fraud-pattern signal. Judged solely on this
        // submission's own attributes, never by comparing it to any other submission —
        // two different people legitimately expensing the same known vendor for the same
        // amount must never trip this.
        var knownVendors = config.GetSection("VendorDirectory").GetChildren()
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (FraudGuard.IsLikelySuspicious(invoice.Vendor, invoice.TotalAmount, knownVendors))
        {
            invoice.Status = InvoiceStatus.Escalated;
            invoice.Reason = $"Blocked: round-number amount to an unrecognized vendor '{invoice.Vendor}' (GLOBAL-FRAUD guardrail).";
            invoice.DecidedBy = DecidedBy.System;

            await daprClient.SaveStateAsync(StateStoreName, GetStateKey(invoice.TrackingId), invoice);
            await submissionStore.MarkSubmittedAsync(invoice.TrackingId);
            await StateIndex.AddAsync(daprClient, StateStoreName, SubmittedIndexKey, invoice.TrackingId);
            await StateIndex.AddAsync(daprClient, StateStoreName, EscalatedIndexKey, invoice.TrackingId);

            logger.LogWarning("{CorrelationId} Invoice {TrackingId} blocked by GLOBAL-FRAUD guardrail (round-number amount, unrecognized vendor).", invoice.TrackingId, invoice.TrackingId);
            return Results.Accepted($"/status/{invoice.TrackingId}", new { invoice.TrackingId });
        }

        await daprClient.SaveStateAsync(StateStoreName, GetStateKey(invoice.TrackingId), invoice);
        await submissionStore.MarkSubmittedAsync(invoice.TrackingId);
        await StateIndex.AddAsync(daprClient, StateStoreName, SubmittedIndexKey, invoice.TrackingId);
        await daprClient.PublishEventAsync(PubSubName, SubmittedTopic, invoice);

        logger.LogInformation("{CorrelationId} Invoice submitted {TrackingId} by {Vendor}.", invoice.TrackingId, invoice.TrackingId, invoice.Vendor);
        return Results.Accepted($"/status/{invoice.TrackingId}", new { invoice.TrackingId });
    }

    // Proxies DecisionEngine's /vendors via Dapr's synchronous service invocation (M5's
    // remaining building block — everything else in this system talks pub/sub or state).
    // The sidecar handles service discovery/mTLS/retries; the Gateway never needs
    // DecisionEngine's network address, only its Dapr app-id.
    private static async Task<IResult> GetVendorsAsync(DaprClient daprClient, ILogger<Program> logger)
    {
        try
        {
            var vendors = await daprClient.InvokeMethodAsync<List<VendorEntry>>(HttpMethod.Get, "decision-engine", "vendors");
            return Results.Ok(vendors);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not reach DecisionEngine for the vendor directory.");
            return Results.Ok(new List<VendorEntry>());
        }
    }

    private static async Task<IResult> GetStatusAsync([FromRoute] string trackingId, DaprClient daprClient, ILogger<Program> logger)
    {
        if (string.IsNullOrWhiteSpace(trackingId))
        {
            return Results.BadRequest(new { error = "trackingId is required." });
        }

        var invoice = await daprClient.GetStateAsync<InvoicePayload>(StateStoreName, GetStateKey(trackingId));
        if (invoice is null || string.IsNullOrEmpty(invoice.TrackingId))
        {
            return Results.NotFound(new { trackingId, message = "Invoice not found." });
        }

        logger.LogInformation("{CorrelationId} Status requested for invoice {TrackingId}.", trackingId, trackingId);
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
            invoice.PaymentStatus,
            invoice.PaymentMessage,
            invoice.SubmittedAt
        });
    }

    // Payment outcome (including Saga compensation) arrives asynchronously from
    // PaymentService; merge it into the invoice record so /status reflects it (F2, F9).
    [Topic(PubSubName, "payment.completed")]
    private static async Task<IResult> HandlePaymentCompletedAsync([FromBody] PaymentResult result, DaprClient daprClient, ILogger<Program> logger)
    {
        var invoice = await daprClient.GetStateAsync<InvoicePayload>(StateStoreName, GetStateKey(result.TrackingId));
        if (invoice is null)
            return Results.Ok();

        invoice.PaymentStatus = result.Success ? "Paid" : "Failed";
        invoice.PaymentMessage = result.Message;
        await daprClient.SaveStateAsync(StateStoreName, GetStateKey(result.TrackingId), invoice);

        logger.LogInformation("{CorrelationId} Payment outcome recorded for invoice {TrackingId}: {PaymentStatus} ({Message}).",
            result.TrackingId, result.TrackingId, invoice.PaymentStatus, result.Message);

        return Results.Ok();
    }

    // F4: queue of only the items the system escalated, so an approver never has to
    // rubber-stamp what the router already handled on its own.
    private static async Task<IResult> GetEscalationsAsync(DaprClient daprClient)
    {
        var trackingIds = await StateIndex.GetAllAsync(daprClient, StateStoreName, EscalatedIndexKey);
        var items = new List<object>();
        foreach (var trackingId in trackingIds)
        {
            var invoice = await daprClient.GetStateAsync<InvoicePayload>(StateStoreName, GetStateKey(trackingId));
            if (invoice is not null && invoice.Status == InvoiceStatus.Escalated)
            {
                items.Add(new
                {
                    invoice.TrackingId,
                    invoice.Vendor,
                    invoice.Category,
                    invoice.AiSuggestedCategory,
                    invoice.AiConfidence,
                    invoice.TotalAmount,
                    invoice.Reason
                });
            }
        }
        return Results.Ok(items);
    }

    // F8: throughput and auto- vs human-approved money, for the controller dashboard.
    private static async Task<IResult> GetStatsAsync(DaprClient daprClient)
    {
        var trackingIds = await StateIndex.GetAllAsync(daprClient, StateStoreName, SubmittedIndexKey);

        int totalSubmitted = 0, autoApproved = 0, humanApproved = 0, escalatedPending = 0, rejected = 0, duplicateRejected = 0;
        decimal autoApprovedAmount = 0, humanApprovedAmount = 0;

        foreach (var trackingId in trackingIds)
        {
            var invoice = await daprClient.GetStateAsync<InvoicePayload>(StateStoreName, GetStateKey(trackingId));
            if (invoice is null) continue;

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
            }
        }

        return Results.Ok(new
        {
            totalSubmitted,
            autoApproved,
            humanApproved,
            escalatedPending,
            rejected,
            duplicateRejected,
            autoApprovedAmount,
            humanApprovedAmount,
            autoApprovalRate = totalSubmitted == 0 ? 0 : Math.Round((double)autoApproved / totalSubmitted, 2)
        });
    }

    private static async Task<IResult> ApproveAsync([FromRoute] string trackingId, DaprClient daprClient, ILogger<Program> logger)
    {
        if (string.IsNullOrWhiteSpace(trackingId))
        {
            return Results.BadRequest(new { error = "trackingId is required." });
        }

        var invoice = await daprClient.GetStateAsync<InvoicePayload>(StateStoreName, GetStateKey(trackingId));
        if (invoice is null || string.IsNullOrEmpty(invoice.TrackingId))
        {
            return Results.NotFound(new { trackingId, message = "Invoice not found." });
        }

        invoice.Status = InvoiceStatus.Approved;
        invoice.Reason = "Manually approved by reviewer.";
        invoice.DecidedBy = DecidedBy.Human;

        await daprClient.SaveStateAsync(StateStoreName, GetStateKey(trackingId), invoice);
        await daprClient.PublishEventAsync(PubSubName, "invoice.decided", new DecisionResult { TrackingId = trackingId, Approved = true, Reason = invoice.Reason, DecidedBy = DecidedBy.Human });
        await daprClient.PublishEventAsync(PubSubName, "invoice.approved", invoice);

        logger.LogInformation("{CorrelationId} Invoice {TrackingId} approved manually.", trackingId, trackingId);
        return Results.Ok(invoice);
    }

    private static async Task<IResult> RejectAsync([FromRoute] string trackingId, DaprClient daprClient, ILogger<Program> logger)
    {
        if (string.IsNullOrWhiteSpace(trackingId))
        {
            return Results.BadRequest(new { error = "trackingId is required." });
        }

        var invoice = await daprClient.GetStateAsync<InvoicePayload>(StateStoreName, GetStateKey(trackingId));
        if (invoice is null || string.IsNullOrEmpty(invoice.TrackingId))
        {
            return Results.NotFound(new { trackingId, message = "Invoice not found." });
        }

        invoice.Status = InvoiceStatus.Rejected;
        invoice.Reason = "Manually rejected by reviewer.";
        invoice.DecidedBy = DecidedBy.Human;

        await daprClient.SaveStateAsync(StateStoreName, GetStateKey(trackingId), invoice);
        await daprClient.PublishEventAsync(PubSubName, "invoice.decided", new DecisionResult { TrackingId = trackingId, Approved = false, Reason = invoice.Reason, DecidedBy = DecidedBy.Human });

        logger.LogInformation("{CorrelationId} Invoice {TrackingId} rejected manually.", trackingId, trackingId);
        return Results.Ok(invoice);
    }

    // Keeps the escalation index in sync for every decision, whether it came from
    // DecisionEngine's automatic evaluation or a manual approve/reject above.
    [Topic(PubSubName, "invoice.decided")]
    private static async Task<IResult> HandleInvoiceDecidedIndexAsync([FromBody] DecisionResult decision, DaprClient daprClient)
    {
        var invoice = await daprClient.GetStateAsync<InvoicePayload>(StateStoreName, GetStateKey(decision.TrackingId));
        if (invoice is null)
            return Results.Ok();

        if (invoice.Status == InvoiceStatus.Escalated)
        {
            await StateIndex.AddAsync(daprClient, StateStoreName, EscalatedIndexKey, decision.TrackingId);
        }
        else
        {
            await StateIndex.RemoveAsync(daprClient, StateStoreName, EscalatedIndexKey, decision.TrackingId);
        }

        return Results.Ok();
    }

    private static string GetStateKey(string trackingId) => $"invoice-{trackingId}";
}
