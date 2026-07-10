using Dapr;
using Dapr.Client;
using DecisionEngine.Ai;
using DecisionEngine.Core.Logic;
using DecisionEngine.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace DecisionEngine.Endpoints;

/// <summary>
/// HTTP/pub-sub surface of the DecisionEngine. Kept separate from Program.cs so the
/// entry point stays a pure composition root (SRP): this file owns request handling,
/// Program.cs owns wiring.
/// </summary>
public static class InvoiceEndpoints
{
    // Matches DaprClient's own default state serialization (camelCase, case-insensitive —
    // verified against the installed Dapr.Client 1.13.0 by reflecting on
    // DaprClientBuilder's default JsonSerializerOptions) so a hand-serialized outbox
    // transaction item round-trips through GetStateAsync<InvoicePayload> identically to
    // one written via the ordinary SaveStateAsync path.
    private static readonly JsonSerializerOptions DaprStateJsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapInvoiceEndpoints(this WebApplication app)
    {
        app.MapPost("/invoice-submitted", HandleInvoiceSubmittedAsync);
        app.MapGet("/vendors", GetVendors);
    }

    [Topic(DaprComponents.PubSub, Topics.InvoiceSubmitted)]
    private static async Task<IResult> HandleInvoiceSubmittedAsync(
        [FromBody] InvoicePayload invoice,
        DaprClient daprClient,
        IAiModelProvider aiProvider,
        PolicyEngine policyEngine,
        ILogger<Program> logger)
    {
        if (invoice is null)
        {
            return Results.BadRequest();
        }

        var trackingId = invoice.TrackingId ?? Guid.NewGuid().ToString();
        invoice.TrackingId = trackingId;

        // Every log line for the rest of this delivery (including EvaluateAndPublishAsync,
        // which inherits the scope across the await chain) carries CorrelationId as a
        // structured field (M14) without repeating it as a message argument each time.
        using var _ = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = trackingId });

        // N4: the same TrackingId every log line already carries, now also on the
        // auto-instrumented ASP.NET Core span for this request — so the trace for one
        // invoice can be found from its logs (search CorrelationId, then correlation_id
        // in Jaeger) and back again.
        Activity.Current?.SetTag("correlation_id", trackingId);

        // At-least-once dedup: a redelivered invoice.submitted must not evaluate twice (a
        // second AI call, and for Travel a second trip-total increment). The Gateway stamps
        // a fresh SubmissionAttemptId per deliberate publish, so a provide-info
        // re-evaluation is distinguishable from a redelivery; TrackingId is the fallback
        // for an event that carries none (safe-by-default: skips rather than double-runs).
        var attemptId = invoice.SubmissionAttemptId ?? trackingId;
        if (!await DecisionAttemptGuard.TryBeginAsync(daprClient, DaprComponents.StateStore, attemptId))
        {
            logger.LogWarning("Duplicate invoice.submitted delivery ignored for invoice {TrackingId} (attempt {AttemptId}).", trackingId, attemptId);
            return Results.Ok();
        }

        try
        {
            var decisionResult = await EvaluateAndPublishAsync(invoice, trackingId, daprClient, aiProvider, policyEngine, logger);

            // Written only after the decision is saved and its events published: a crash
            // anywhere above leaves no marker, so Dapr's redelivery retries the attempt
            // (the claim below is released in the finally) instead of stranding the
            // invoice in Pending forever.
            await DecisionAttemptGuard.MarkDecidedAsync(daprClient, DaprComponents.StateStore, attemptId);

            return Results.Ok(decisionResult);
        }
        finally
        {
            await DecisionAttemptGuard.ReleaseClaimAsync(daprClient, DaprComponents.StateStore, attemptId);
        }
    }

    private static async Task<DecisionResult> EvaluateAndPublishAsync(
        InvoicePayload invoice,
        string trackingId,
        DaprClient daprClient,
        IAiModelProvider aiProvider,
        PolicyEngine policyEngine,
        ILogger<Program> logger)
    {
        // N4: one span wrapping the whole route-and-decide step, child of the request's
        // auto-instrumented span — lets a trace show "how long did deciding this invoice
        // take" as its own bar, separate from the outbox write and the invoice.approved
        // publish that follow it.
        using var activity = DecisionEngineTelemetry.ActivitySource.StartActivity("policy.evaluate");
        activity?.SetTag("correlation_id", trackingId);
        activity?.SetTag("invoice.vendor", invoice.Vendor);

        // The risk threshold and GLOBAL-RECEIPT/GLOBAL-MATH never depend on category, so
        // don't spend an AI call (cost, latency, rate limit) on an invoice that's escalating
        // on amount/receipt grounds alone regardless of what the AI would say.
        RouterDecision decision;
        string decidedBy;
        var fastReject = policyEngine.TryFastRejectOnGlobalGuardrails(invoice);
        if (fastReject is not null)
        {
            decision = fastReject;
            decidedBy = DecidedBy.System;
            logger.LogInformation("Invoice {TrackingId} failed a global guardrail; skipping AI classification.", trackingId);
        }
        else
        {
            // GLOBAL-VENDOR (part of the guardrail check just above) guarantees the vendor
            // is in VendorDirectory at this point, so the category is a deterministic
            // lookup — the AI is no longer asked to guess it, only to judge whether this
            // submission's content is coherent with it.
            var category = policyEngine.ResolveVendorCategory(invoice.Vendor);
            if (category is null)
            {
                // Defense in depth only — should be unreachable, since the guardrail check
                // above already enforces GLOBAL-VENDOR.
                decision = RouterDecision.Escalated($"Vendor '{invoice.Vendor}' has no directory category.");
                decidedBy = DecidedBy.System;
            }
            else
            {
                invoice.AiSuggestedCategory = category;
                try
                {
                    // N4: "the agent's model/tool calls" the assignment asks to be part of
                    // the trace — a distinct child span so a trace viewer shows exactly how
                    // long the AI call itself took, separate from PolicyEngine's own
                    // deterministic evaluation right after it (ActivityKind.Client: this is
                    // this service acting as a client of an external-ish dependency, whether
                    // that's the real Groq HTTP call or the in-process Stub).
                    using var aiActivity = DecisionEngineTelemetry.ActivitySource.StartActivity("ai.analyze_invoice", ActivityKind.Client);
                    aiActivity?.SetTag("correlation_id", trackingId);
                    aiActivity?.SetTag("ai.vendor_category", category);

                    var aiResult = await aiProvider.AnalyzeAsync(invoice, category, CancellationToken.None);

                    // Structured facts the submitter typed directly always win over anything the
                    // AI might otherwise infer from free-text Notes (no OCR — see TripId on
                    // InvoicePayload).
                    if (!string.IsNullOrWhiteSpace(invoice.TripId))
                        aiResult.LinkedTripId = invoice.TripId;

                    aiActivity?.SetTag("ai.confidence_score", aiResult.ConfidenceScore);
                    aiActivity?.SetTag("ai.linked_trip_id", aiResult.LinkedTripId);
                    aiActivity?.SetTag("ai.policy_rules_cited", string.Join(",", aiResult.PolicyRulesCited));

                    invoice.AiConfidence = aiResult.ConfidenceScore;
                    // N5/F4: whichever docs/policy.md rule(s) PolicyRetriever surfaced and
                    // the AI actually cited — visible on the invoice itself (status,
                    // escalations, audit trail), not just in the trace/log line.
                    invoice.AiPolicyRulesCited = aiResult.PolicyRulesCited;
                    decision = await policyEngine.EvaluateAsync(invoice, category, aiResult);
                    decidedBy = DecidedBy.Ai;
                }
                catch (Exception ex)
                {
                    // Fail-fast, never silent: an AI/provider error always escalates, never auto-approves (M15).
                    logger.LogError(ex, "AI provider failed for invoice {TrackingId}; escalating for safety.", trackingId);
                    decision = RouterDecision.Escalated("AI provider error — escalated for safety.");
                    decidedBy = DecidedBy.System;
                }
            }
        }

        var approved = decision.IsApproved;
        var reason = decision.Reason;

        invoice.Status = approved ? InvoiceStatus.Approved : InvoiceStatus.Escalated;
        invoice.Reason = reason;
        invoice.DecidedBy = decidedBy;

        // N3 outbox (components/statestore.yaml): one call both persists the decision and
        // publishes invoice.decided, atomically — replacing the previous SaveStateAsync +
        // PublishEventAsync(decisionResult) pair, which could save the decision and then
        // crash before the event fired, stranding the invoice in a "decided but nobody was
        // told" limbo (stale escalation index, an SSE listener that waits forever).
        await daprClient.ExecuteStateTransactionAsync(DaprComponents.StateStore,
        [
            new StateTransactionRequest(StateKeys.Invoice(trackingId), JsonSerializer.SerializeToUtf8Bytes(invoice, DaprStateJsonOptions), StateOperationType.Upsert)
        ]);

        var decisionResult = new DecisionResult
        {
            TrackingId = trackingId,
            Approved = approved,
            Reason = reason,
            DecidedBy = decidedBy
        };

        logger.LogInformation("Decision made for invoice {TrackingId}: {Approved} ({Reason})", trackingId, approved, reason);

        if (approved)
        {
            await daprClient.PublishEventAsync(DaprComponents.PubSub, Topics.InvoiceApproved, invoice);
        }

        return decisionResult;
    }

    // Known vendors the Stub classifier can confidently map to a category, so a submitter
    // (or the UI) can see which names actually give the router a strong signal.
    private static IResult GetVendors(IConfiguration config)
    {
        var vendors = config.GetSection(VendorDirectory.SectionName).GetChildren()
            .Select(entry => new VendorEntry(entry.Key, entry.Value ?? string.Empty))
            .OrderBy(v => v.Vendor)
            .ToList();

        return Results.Ok(vendors);
    }

}
