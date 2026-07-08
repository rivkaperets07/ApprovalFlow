using Dapr;
using Dapr.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

var app = builder.Build();

app.UseCors();
app.UseCloudEvents();
app.MapSubscribeHandler();

const string StateStoreName = "statestore";
const string PubSubName = "pubsub";
const string SubmittedTopic = "invoice.submitted";

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
        invoice.TotalAmount
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
    if (invoice is null || string.IsNullOrEmpty(invoice.TrackingId))
    {
        return Results.NotFound(new { trackingId, message = "Invoice not found." });
    }

    invoice.Status = InvoiceStatus.Rejected;
    invoice.Reason = "Manually rejected by reviewer.";

    await daprClient.SaveStateAsync(StateStoreName, GetStateKey(trackingId), invoice);
    await daprClient.PublishEventAsync(PubSubName, "invoice.decided", new DecisionResult { TrackingId = trackingId, Approved = false, Reason = invoice.Reason });

    logger.LogInformation("{CorrelationId} Invoice {TrackingId} rejected manually.", trackingId, trackingId);
    return Results.Ok(invoice);
});

app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

app.Run();

static string GetStateKey(string trackingId) => $"invoice-{trackingId}";

public class DecisionResult
{
    public string TrackingId { get; set; } = string.Empty;
    public bool Approved { get; set; }
    public string Reason { get; set; } = string.Empty;
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
}

