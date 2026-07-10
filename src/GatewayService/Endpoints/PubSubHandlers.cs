using Dapr;
using Dapr.Client;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Text.Json;

/// <summary>
/// Dapr sidecar deliveries — anonymous on purpose: the sidecar carries no JWT, and these
/// routes are only reachable inside the compose network (M6, single published port
/// notwithstanding, they're not part of the public API surface). Split out of the former
/// single GatewayEndpoints.cs (SRP); see SubmissionEndpoints and ApproverEndpoints for the
/// two authenticated surfaces.
/// </summary>
public static class PubSubHandlers
{
    public static void MapPubSubHandlers(this WebApplication app)
    {
        app.MapPost("/payment-completed", HandlePaymentCompletedAsync);
        app.MapPost("/invoice-decided-index", HandleInvoiceDecidedIndexAsync);
    }

    // Payment outcome (including Saga compensation) arrives asynchronously from
    // PaymentService; merge it into the invoice record so /status reflects it (F2, F9).
    // The merge is ETag-guarded with a short retry: a plain read-modify-write here would
    // silently drop whichever concurrent update (this merge or another writer of the
    // record) lost the race — the same lost-update class StateIndex already defends against.
    [Topic(DaprComponents.PubSub, Topics.PaymentCompleted)]
    private static async Task<IResult> HandlePaymentCompletedAsync([FromBody] PaymentResult result, DaprClient daprClient, ILogger<Program> logger)
    {
        using var _ = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = result.TrackingId });

        const int maxAttempts = 5;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var (invoice, etag) = await daprClient.GetStateAndETagAsync<InvoicePayload>(DaprComponents.StateStore, StateKeys.Invoice(result.TrackingId));
            if (invoice is null)
                return Results.Ok();

            invoice.PaymentStatus = result.Success ? "Paid" : "Failed";
            invoice.PaymentMessage = result.Message;
            if (await daprClient.TrySaveStateAsync(DaprComponents.StateStore, StateKeys.Invoice(result.TrackingId), invoice, etag))
            {
                logger.LogInformation("Payment outcome recorded for invoice {TrackingId}: {PaymentStatus} ({Message}).",
                    result.TrackingId, invoice.PaymentStatus, result.Message);
                return Results.Ok();
            }
        }

        // Non-2xx on purpose: Dapr redelivers the event, so the payment outcome is merged
        // on a later attempt instead of being silently lost.
        logger.LogWarning("Could not merge payment outcome for invoice {TrackingId} after {MaxAttempts} ETag conflicts; leaving for redelivery.",
            result.TrackingId, maxAttempts);
        return Results.Problem();
    }

    // Keeps the escalation index in sync for every decision, whether it came from
    // DecisionEngine's automatic evaluation (published via the N3 outbox — see
    // components/statestore.yaml) or a manual approve/reject/request-info in
    // ApproverEndpoints (still a plain PublishEventAsync). Also fires M8's notification
    // channel (IInvoiceNotifier) so anyone holding open GET /notifications/{trackingId}
    // gets woken up instead of having to poll.
    //
    // Reads the body manually instead of a typed [FromBody] parameter: an outbox-published
    // event's CloudEvent doesn't declare datacontenttype as application/json (Dapr has no
    // way to know the raw state bytes it's forwarding are JSON), so ASP.NET Core's JSON
    // model binder 415s on it even though the body genuinely is JSON. TrackingId is the
    // only field this handler needs, and it's present on every shape that reaches this
    // topic — the small DecisionResult DTO ApproverEndpoints still publishes, and the full
    // InvoicePayload the outbox now publishes for DecisionEngine's own decisions.
    [Topic(DaprComponents.PubSub, Topics.InvoiceDecided)]
    private static async Task<IResult> HandleInvoiceDecidedIndexAsync(HttpContext context, DaprClient daprClient, IInvoiceNotifier notifier)
    {
        using var body = await JsonDocument.ParseAsync(context.Request.Body);
        var trackingId = body.RootElement.TryGetProperty("trackingId", out var idProp) ? idProp.GetString() : null;
        if (string.IsNullOrEmpty(trackingId))
            return Results.Ok();

        var invoice = await daprClient.GetStateAsync<InvoicePayload>(DaprComponents.StateStore, StateKeys.Invoice(trackingId));
        if (invoice is null)
            return Results.Ok();

        if (invoice.Status == InvoiceStatus.Escalated)
        {
            await StateIndex.AddAsync(daprClient, DaprComponents.StateStore, GatewayIndexKeys.Escalated, trackingId);
        }
        else
        {
            await StateIndex.RemoveAsync(daprClient, DaprComponents.StateStore, GatewayIndexKeys.Escalated, trackingId);
        }

        notifier.Publish(trackingId, new { invoice.TrackingId, invoice.Status });

        return Results.Ok();
    }
}
