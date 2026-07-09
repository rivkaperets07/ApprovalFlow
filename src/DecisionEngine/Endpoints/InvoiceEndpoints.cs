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

namespace DecisionEngine.Endpoints;

/// <summary>
/// HTTP/pub-sub surface of the DecisionEngine. Kept separate from Program.cs so the
/// entry point stays a pure composition root (SRP): this file owns request handling,
/// Program.cs owns wiring.
/// </summary>
public static class InvoiceEndpoints
{
    private const string StateStoreName = "statestore";
    private const string PubSubName = "pubsub";

    public static void MapInvoiceEndpoints(this WebApplication app)
    {
        app.MapPost("/invoice-submitted", HandleInvoiceSubmittedAsync);
        app.MapGet("/vendors", GetVendors);
    }

    [Topic(PubSubName, "invoice.submitted")]
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
            logger.LogInformation("{CorrelationId} Invoice {TrackingId} failed a global guardrail; skipping AI classification.", trackingId, trackingId);
        }
        else
        {
            try
            {
                var aiResult = await aiProvider.AnalyzeAsync(invoice, CancellationToken.None);

                // Structured facts the submitter typed directly always win over anything the
                // AI might otherwise infer from free-text Notes (no OCR — see TripId on
                // InvoicePayload).
                if (!string.IsNullOrWhiteSpace(invoice.TripId))
                    aiResult.LinkedTripId = invoice.TripId;

                invoice.AiSuggestedCategory = aiResult.SuggestedCategory;
                invoice.AiConfidence = aiResult.ConfidenceScore;
                decision = await policyEngine.EvaluateAsync(invoice, aiResult);
                decidedBy = DecidedBy.Ai;
            }
            catch (Exception ex)
            {
                // Fail-fast, never silent: an AI/provider error always escalates, never auto-approves (M15).
                logger.LogError(ex, "{CorrelationId} AI provider failed for invoice {TrackingId}; escalating for safety.", trackingId, trackingId);
                decision = RouterDecision.Escalated("AI provider error — escalated for safety.");
                decidedBy = DecidedBy.System;
            }
        }

        var approved = decision.IsApproved;
        var reason = decision.Reason;

        invoice.Status = approved ? InvoiceStatus.Approved : InvoiceStatus.Escalated;
        invoice.Reason = reason;
        invoice.DecidedBy = decidedBy;

        await daprClient.SaveStateAsync(StateStoreName, GetStateKey(trackingId), invoice);

        var decisionResult = new DecisionResult
        {
            TrackingId = trackingId,
            Approved = approved,
            Reason = reason,
            DecidedBy = decidedBy
        };

        await daprClient.PublishEventAsync(PubSubName, "invoice.decided", decisionResult);

        logger.LogInformation("{CorrelationId} Decision made for invoice {TrackingId}: {Approved} ({Reason})", trackingId, trackingId, approved, reason);

        if (approved)
        {
            await daprClient.PublishEventAsync(PubSubName, "invoice.approved", invoice);
        }

        return Results.Ok(decisionResult);
    }

    // Known vendors the Stub classifier can confidently map to a category, so a submitter
    // (or the UI) can see which names actually give the router a strong signal.
    private static IResult GetVendors(IConfiguration config)
    {
        var vendors = config.GetSection("VendorDirectory").GetChildren()
            .Select(entry => new VendorEntry(entry.Key, entry.Value ?? string.Empty))
            .OrderBy(v => v.Vendor)
            .ToList();

        return Results.Ok(vendors);
    }

    private static string GetStateKey(string trackingId) => $"invoice-{trackingId}";
}
