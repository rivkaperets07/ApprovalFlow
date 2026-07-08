using Dapr;
using Dapr.Client;
using DecisionEngine.Ai;
using DecisionEngine.Core.Logic;
using DecisionEngine.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("Policies/policies.json", optional: false, reloadOnChange: true);

builder.Services.AddDaprClient();
builder.Services.AddLogging();
builder.Services.AddSingleton<PolicyEngine>();

var aiProviderName = builder.Configuration.GetValue<string>("AiProvider", "Stub");
if (string.Equals(aiProviderName, "Groq", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient<IAiModelProvider, GroqAiModelProvider>();
}
else
{
    builder.Services.AddSingleton<IAiModelProvider, StubAiModelProvider>();
}

var app = builder.Build();
app.UseCloudEvents();
app.MapSubscribeHandler();

const string StateStoreName = "statestore";
const string PubSubName = "pubsub";

app.MapPost("/invoice-submitted", [Topic(PubSubName, "invoice.submitted")] async (
    [FromBody] InvoicePayload invoice,
    DaprClient daprClient,
    IAiModelProvider aiProvider,
    PolicyEngine policyEngine,
    ILogger<Program> logger) =>
{
    if (invoice is null)
    {
        return Results.BadRequest();
    }

    var trackingId = invoice.TrackingId ?? Guid.NewGuid().ToString();
    invoice.TrackingId = trackingId;

    RouterDecision decision;
    try
    {
        var aiResult = await aiProvider.AnalyzeAsync(invoice, CancellationToken.None);
        decision = await policyEngine.EvaluateAsync(invoice, aiResult);
    }
    catch (Exception ex)
    {
        // Fail-fast, never silent: an AI/provider error always escalates, never auto-approves (M15).
        logger.LogError(ex, "{CorrelationId} AI provider failed for invoice {TrackingId}; escalating for safety.", trackingId, trackingId);
        decision = RouterDecision.Escalated("AI provider error — escalated for safety.");
    }

    var approved = decision.IsApproved;
    var reason = decision.Reason;

    invoice.Status = approved ? InvoiceStatus.Approved : InvoiceStatus.Escalated;
    invoice.Reason = reason;

    await daprClient.SaveStateAsync(StateStoreName, GetStateKey(trackingId), invoice);

    var decisionResult = new DecisionResult
    {
        TrackingId = trackingId,
        Approved = approved,
        Reason = reason
    };

    await daprClient.PublishEventAsync(PubSubName, "invoice.decided", decisionResult);

    logger.LogInformation("{CorrelationId} Decision made for invoice {TrackingId}: {Approved} ({Reason})", trackingId, trackingId, approved, reason);

    if (approved)
    {
        await daprClient.PublishEventAsync(PubSubName, "invoice.approved", invoice);
    }

    return Results.Ok(decisionResult);
});

app.MapPost("/approve/{trackingId}", async ([FromRoute] string trackingId, DaprClient daprClient, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(trackingId))
    {
        return Results.BadRequest(new { error = "trackingId is required." });
    }

    var invoice = await daprClient.GetStateAsync<InvoicePayload>(StateStoreName, GetStateKey(trackingId));
    if (invoice is null)
    {
        return Results.NotFound(new { trackingId, message = "Invoice not found." });
    }

    invoice.Status = InvoiceStatus.Approved;
    invoice.Reason = "Manually approved.";
    await daprClient.SaveStateAsync(StateStoreName, GetStateKey(trackingId), invoice);
    await daprClient.PublishEventAsync(PubSubName, "invoice.decided", new DecisionResult { TrackingId = trackingId, Approved = true, Reason = invoice.Reason });
    await daprClient.PublishEventAsync(PubSubName, "invoice.approved", invoice);

    logger.LogInformation("{CorrelationId} Invoice {TrackingId} approved manually.", trackingId, trackingId);
    return Results.Ok(invoice);
});

app.MapPost("/reject/{trackingId}", async ([FromRoute] string trackingId, DaprClient daprClient, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(trackingId))
    {
        return Results.BadRequest(new { error = "trackingId is required." });
    }

    var invoice = await daprClient.GetStateAsync<InvoicePayload>(StateStoreName, GetStateKey(trackingId));
    if (invoice is null)
    {
        return Results.NotFound(new { trackingId, message = "Invoice not found." });
    }

    invoice.Status = InvoiceStatus.Rejected;
    invoice.Reason = "Manually rejected.";
    await daprClient.SaveStateAsync(StateStoreName, GetStateKey(trackingId), invoice);
    await daprClient.PublishEventAsync(PubSubName, "invoice.decided", new DecisionResult { TrackingId = trackingId, Approved = false, Reason = invoice.Reason });

    logger.LogInformation("{CorrelationId} Invoice {TrackingId} rejected manually.", trackingId, trackingId);
    return Results.Ok(invoice);
});

app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

app.Run();

static string GetStateKey(string trackingId) => $"invoice-{trackingId}";

public static class InvoiceStatus
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Escalated = "Escalated";
    public const string Rejected = "Rejected";
}

public class InvoicePayload
{
    public string? TrackingId { get; set; }
    public string Vendor { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public string Category { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public string? Status { get; set; }
    public string? Reason { get; set; }
}

public class DecisionResult
{
    public string TrackingId { get; set; } = string.Empty;
    public bool Approved { get; set; }
    public string Reason { get; set; } = string.Empty;
}
