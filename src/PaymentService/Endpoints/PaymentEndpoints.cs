using Dapr;
using Dapr.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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

    [Topic(PaymentConstants.PUBSUB_NAME, PaymentConstants.APPROVED_TOPIC)]
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
        logger.LogInformation("{CorrelationId} Payment request received for invoice {TrackingId} amount {Amount:C}.", invoice.TrackingId, invoice.TrackingId, invoice.TotalAmount);

        var result = await paymentProcessor.ProcessAsync(invoice, logger, daprClient);

        // Surface the payment outcome back to the Gateway (F2, F9): without this, a failed
        // or compensated payment would be invisible to anyone polling /status.
        await daprClient.PublishEventAsync(PaymentConstants.PUBSUB_NAME, "payment.completed", result);

        return Results.Ok(result);
    }
}
