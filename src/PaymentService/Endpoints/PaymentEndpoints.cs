using Dapr;
using Dapr.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

/// <summary>
/// Pub/sub surface of the PaymentService. Kept out of Program.cs so the entry point is a
/// pure composition root (SRP): this file owns event handling, Program.cs owns wiring.
/// </summary>
public static class PaymentEndpoints
{
    public static void MapPaymentEndpoints(this WebApplication app)
    {
        app.MapPost("/process-payment", ProcessPaymentAsync);
    }

    [Topic(DaprComponents.PubSub, Topics.InvoiceApproved)]
    private static async Task<IResult> ProcessPaymentAsync(
        [FromBody] InvoicePayload invoice,
        IPaymentProcessor paymentProcessor,
        DaprClient daprClient,
        ILogger<Program> logger)
    {
        if (invoice is null)
        {
            logger.LogWarning("Received null invoice payload.");
            return Results.BadRequest();
        }

        invoice.TrackingId ??= Guid.NewGuid().ToString();

        // Scopes are shared across every ILogger<T> in the app (they're keyed by the
        // logging provider, not the category), so this covers PaymentProcessor's own log
        // calls too — one CorrelationId field on every line for this payment, no matter
        // which class logs it, without threading it through ProcessAsync's signature.
        using var _ = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = invoice.TrackingId });

        // N4: this request's span is the last leg of the distributed trace that started
        // with Gateway's /submit — tagging it with the same id logs already carry (M14)
        // means the whole chain, start to finish, is one searchable trace in Jaeger.
        Activity.Current?.SetTag("correlation_id", invoice.TrackingId);

        logger.LogInformation("Payment request received for invoice {TrackingId} amount {Amount:C}.", invoice.TrackingId, invoice.TotalAmount);

        var result = await paymentProcessor.ProcessAsync(invoice);

        // Surface the payment outcome back to the Gateway (F2, F9): without this, a failed
        // or compensated payment would be invisible to anyone polling /status.
        await daprClient.PublishEventAsync(DaprComponents.PubSub, Topics.PaymentCompleted, result);

        return Results.Ok(result);
    }
}
