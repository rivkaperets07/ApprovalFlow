using Dapr;
using Dapr.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDaprClient();
builder.Services.AddLogging();

var app = builder.Build();
app.UseCloudEvents();
app.MapSubscribeHandler();

// Configurable ceiling for auto-approval (can be set via env AUTO_APPROVE_CEILING)
var ceiling = builder.Configuration.GetValue<decimal>("AUTO_APPROVE_CEILING", 1000m);

const string StateStoreName = "statestore";
const string PubSubName = "pubsub";

app.MapPost("/invoice-submitted", [Topic(PubSubName, "invoice.submitted")] async ([FromBody] InvoicePayload invoice, DaprClient daprClient, ILogger<Program> logger) =>
{
    if (invoice is null)
    {
        return Results.BadRequest();
    }

    var trackingId = invoice.TrackingId ?? Guid.NewGuid().ToString();

    bool approved = invoice.TotalAmount <= ceiling;
    var reason = approved ? $"Auto-approved under ceiling {ceiling:C}." : "Escalated for human review.";

    // Update invoice status and persist
    invoice.TrackingId = trackingId;
    invoice.Status = approved ? InvoiceStatus.Approved : InvoiceStatus.Escalated;
    invoice.Reason = reason;

    await daprClient.SaveStateAsync(StateStoreName, GetStateKey(trackingId), invoice);

    var decision = new DecisionResult
    {
        TrackingId = trackingId,
        Approved = approved,
        Reason = reason
    };

    // Publish the decision event
    await daprClient.PublishEventAsync(PubSubName, "invoice.decided", decision);

    logger.LogInformation("{{CorrelationId}} Decision made for invoice {trackingId}: {approved} ({reason})", trackingId, approved, reason);

    // If approved, also publish to approved topic so payment service can process
    if (approved)
    {
        await daprClient.PublishEventAsync(PubSubName, "invoice.approved", invoice);
    }

    return Results.Ok();
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

    logger.LogInformation("{{CorrelationId}} Invoice {trackingId} approved manually.", trackingId);
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

    logger.LogInformation("{{CorrelationId}} Invoice {trackingId} rejected manually.", trackingId);
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

