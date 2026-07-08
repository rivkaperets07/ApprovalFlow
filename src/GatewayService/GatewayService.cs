using System.Threading.RateLimiting;
using Dapr;
using Dapr.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

var daprHttpEndpoint = builder.Configuration.GetValue<string>("DAPR_HTTP_ENDPOINT", "http://localhost:3500");
builder.Services.AddDaprClient(client => client.UseHttpEndpoint(daprHttpEndpoint));
builder.Services.AddSingleton<IDaprStateClient, DaprStateClient>();
builder.Services.AddSingleton<ISubmissionStore, DaprSubmissionStore>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Gateway is the single external entry point (M6), so rate limiting lives here: per-client
// IP, fixed window. Requests over the limit get a 429 instead of being queued indefinitely.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromSeconds(10),
                QueueLimit = 0
            }));
});

var app = builder.Build();

app.UseCors();
app.UseRateLimiter();
app.UseCloudEvents();
app.MapSubscribeHandler();

const string StateStoreName = "statestore";
const string PubSubName = "pubsub";
const string SubmittedTopic = "invoice.submitted";
const string SubmittedIndexKey = "index-submitted";
const string EscalatedIndexKey = "index-escalated";

app.MapPost("/submit", async ([FromBody] InvoicePayload invoice, DaprClient daprClient, ISubmissionStore submissionStore, ILogger<Program> logger) =>
{
    if (invoice is null || string.IsNullOrWhiteSpace(invoice.Vendor) || invoice.TotalAmount <= 0)
    {
        logger.LogWarning("Invalid submission received.");
        return Results.BadRequest(new { error = "Vendor, category and TotalAmount must be provided." });
    }

    invoice.TrackingId ??= Guid.NewGuid().ToString();
    invoice.Status = InvoiceStatus.Pending;
    invoice.Reason = "Submission received and published for decision.";

    var alreadySubmitted = await submissionStore.HasBeenSubmittedAsync(invoice.TrackingId);
    if (alreadySubmitted)
    {
        logger.LogWarning("{CorrelationId} Duplicate submission ignored for invoice {TrackingId}.", invoice.TrackingId, invoice.TrackingId);
        return Results.Accepted($"/status/{invoice.TrackingId}", new { invoice.TrackingId });
    }

    await daprClient.SaveStateAsync(StateStoreName, GetStateKey(invoice.TrackingId), invoice);
    await submissionStore.MarkSubmittedAsync(invoice.TrackingId);
    await StateIndex.AddAsync(daprClient, StateStoreName, SubmittedIndexKey, invoice.TrackingId);
    await daprClient.PublishEventAsync(PubSubName, SubmittedTopic, invoice);

    logger.LogInformation("{CorrelationId} Invoice submitted {TrackingId} by {Vendor}.", invoice.TrackingId, invoice.TrackingId, invoice.Vendor);
    return Results.Accepted($"/status/{invoice.TrackingId}", new { invoice.TrackingId });
});

app.MapGet("/status/{trackingId}", async ([FromRoute] string trackingId, DaprClient daprClient, ILogger<Program> logger) =>
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
        invoice.DecidedBy
    });
});

// F4: queue of only the items the system escalated, so an approver never has to
// rubber-stamp what the router already handled on its own.
app.MapGet("/escalations", async (DaprClient daprClient) =>
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
                invoice.TotalAmount,
                invoice.Reason
            });
        }
    }
    return Results.Ok(items);
});

// F8: throughput and auto- vs human-approved money, for the controller dashboard.
app.MapGet("/stats", async (DaprClient daprClient) =>
{
    var trackingIds = await StateIndex.GetAllAsync(daprClient, StateStoreName, SubmittedIndexKey);

    int totalSubmitted = 0, autoApproved = 0, humanApproved = 0, escalatedPending = 0, rejected = 0;
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
        }
    }

    return Results.Ok(new
    {
        totalSubmitted,
        autoApproved,
        humanApproved,
        escalatedPending,
        rejected,
        autoApprovedAmount,
        humanApprovedAmount,
        autoApprovalRate = totalSubmitted == 0 ? 0 : Math.Round((double)autoApproved / totalSubmitted, 2)
    });
});

app.MapPost("/approve/{trackingId}", async ([FromRoute] string trackingId, DaprClient daprClient, ILogger<Program> logger) =>
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
});

app.MapPost("/reject/{trackingId}", async ([FromRoute] string trackingId, DaprClient daprClient, ILogger<Program> logger) =>
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
});

// Keeps the escalation index in sync for every decision, whether it came from
// DecisionEngine's automatic evaluation or a manual approve/reject above.
app.MapPost("/invoice-decided-index", [Topic(PubSubName, "invoice.decided")] async ([FromBody] DecisionResult decision, DaprClient daprClient) =>
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
});

app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

app.Run();

static string GetStateKey(string trackingId) => $"invoice-{trackingId}";

public static class DecidedBy
{
    public const string Ai = "AI";
    public const string Human = "Human";
}

public class DecisionResult
{
    public string TrackingId { get; set; } = string.Empty;
    public bool Approved { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? DecidedBy { get; set; }
}

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
    public string? DecidedBy { get; set; }
}
